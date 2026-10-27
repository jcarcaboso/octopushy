use std::io::IsTerminal;
use std::process::ExitCode;

use clap::Parser;
use octopushy::cli::{AgentCommand, Cli, Command};
use octopushy::enrollment::{self, EnrollOptions, EnrollmentToken};
use octopushy::paths::{StatePaths, default_state_dir};
use octopushy::run::{RunOptions, run};
use tokio_util::sync::CancellationToken;

fn init_tracing() {
    let filter = std::env::var("OCTOPUSHY_LOG")
        .ok()
        .and_then(|f| tracing_subscriber::EnvFilter::try_new(f).ok())
        .or_else(|| tracing_subscriber::EnvFilter::try_from_default_env().ok())
        .unwrap_or_else(|| tracing_subscriber::EnvFilter::new("info"));
    tracing_subscriber::fmt()
        .with_env_filter(filter)
        .with_writer(std::io::stderr)
        .with_ansi(std::io::stderr().is_terminal())
        .with_target(false)
        .init();
}

/// Cancels `token` on the first SIGTERM/SIGINT; exits on the second.
fn handle_signals(token: CancellationToken) {
    use tokio::signal::unix::{SignalKind, signal};
    tokio::spawn(async move {
        let (Ok(mut term), Ok(mut int)) = (
            signal(SignalKind::terminate()),
            signal(SignalKind::interrupt()),
        ) else {
            tracing::error!("cannot install signal handlers");
            return;
        };
        tokio::select! { _ = term.recv() => {}, _ = int.recv() => {} }
        tracing::info!("signal received; draining (signal again to exit at once)");
        token.cancel();
        tokio::select! { _ = term.recv() => {}, _ = int.recv() => {} }
        tracing::warn!("second signal; exiting now");
        std::process::exit(130);
    });
}

async fn agent(command: AgentCommand) -> anyhow::Result<()> {
    match command {
        AgentCommand::ManagedReporting => octopushy::managed_reporting::configure_stdin(),
        AgentCommand::ManagedIdentity { command } => {
            use base64::Engine;
            use octopushy::{cli::ManagedIdentityCommand, managed_identity};
            let report = match command {
                ManagedIdentityCommand::Prepare(a) => {
                    managed_identity::prepare(&StatePaths::new(a.state_dir), &a.generation)?
                }
                ManagedIdentityCommand::Install(a) => {
                    anyhow::ensure!(
                        a.payload_base64.len() <= 45000,
                        "public payload is too large"
                    );
                    let public =
                        base64::engine::general_purpose::STANDARD.decode(a.payload_base64)?;
                    managed_identity::install(
                        &StatePaths::new(a.state_dir),
                        &a.generation,
                        std::str::from_utf8(&public)?,
                    )?
                }
            };
            println!("{report}");
            Ok(())
        }
        AgentCommand::Enroll(args) => {
            let token = EnrollmentToken::parse(&args.token)?;
            let state_dir = args.state_dir.unwrap_or_else(default_state_dir);
            let record = enrollment::enroll(EnrollOptions {
                server: args.server,
                token,
                name: args.name,
                state_dir: state_dir.clone(),
                force: args.force,
            })
            .await?;
            println!(
                "enrolled as {} ({}) with {}; state in {}",
                record.agent_id,
                record.name,
                record.server_url,
                state_dir.display()
            );
            Ok(())
        }
        AgentCommand::Run(args) => {
            let mut opts = RunOptions::new(args.state_dir.unwrap_or_else(default_state_dir));
            opts.work_dir = args.work_dir;
            opts.tool_runtime = args.tool_runtime;
            opts.tofu_binary = args.tofu_binary;
            opts.container_binary = args.container_binary;
            opts.container_network = args.container_network;
            let shutdown = CancellationToken::new();
            handle_signals(shutdown.clone());
            run(opts, shutdown).await
        }
        AgentCommand::Status(args) => octopushy::status::print(&StatePaths::new(
            args.state_dir.unwrap_or_else(default_state_dir),
        )),
    }
}

#[tokio::main]
async fn main() -> ExitCode {
    let cli = Cli::parse();
    init_tracing();
    let result = match cli.command {
        Command::Operator { config, command } => octopushy::operator::run(config, command).await,
        Command::Version => {
            println!("octopushy {}", octopushy::VERSION);
            Ok(())
        }
        Command::Agent { command } => agent(command).await,
    };
    match result {
        Ok(()) => ExitCode::SUCCESS,
        Err(e) => {
            eprintln!("error: {e:#}");
            ExitCode::FAILURE
        }
    }
}
