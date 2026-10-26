//! Command-line interface. Agent commands live under `octopushy agent`, so a
//! `octopushy supervisor` mode can be added next to them.

use std::path::PathBuf;

use clap::{Args, Parser, Subcommand};

use crate::tools::RuntimeKind;

#[derive(Debug, Parser)]
#[command(name = "octopushy", version = crate::VERSION, about = "Octopushy homelab control plane agent")]
pub struct Cli {
    #[command(subcommand)]
    pub command: Command,
}

#[derive(Debug, Subcommand)]
pub enum Command {
    /// Run or manage this machine's agent.
    Agent {
        #[command(subcommand)]
        command: AgentCommand,
    },
    /// Print the version.
    Version,
}

#[derive(Debug, Subcommand)]
pub enum AgentCommand {
    /// Enroll with a server using a one-time token.
    Enroll(EnrollArgs),
    /// Connect to the server and execute assignments.
    Run(RunArgs),
    /// Fixed public-CSR managed worker bootstrap (no bearer token).
    ManagedIdentity {
        #[command(subcommand)]
        command: ManagedIdentityCommand,
    },
    /// Show the stored enrollment and pending work.
    Status(StatusArgs),
}

#[derive(Debug, Args)]
pub struct EnrollArgs {
    /// The server's agent endpoint, such as https://octopushy.lan:8443.
    #[arg(long)]
    pub server: String,
    /// The enrollment token `opy1.<secret>.<fingerprint>`. Prefer the
    /// environment variable: arguments are visible to other local users.
    #[arg(long, env = "OCTOPUSHY_ENROLLMENT_TOKEN", hide_env_values = true)]
    pub token: String,
    /// The name to be shown as (default: the hostname).
    #[arg(long)]
    pub name: Option<String>,
    /// State directory (default: $STATE_DIRECTORY, /var/lib/octopushy-agent for
    /// root, else $XDG_STATE_HOME/octopushy-agent).
    #[arg(long)]
    pub state_dir: Option<PathBuf>,
    /// Replace an existing enrollment.
    #[arg(long)]
    pub force: bool,
}

#[derive(Debug, Args)]
pub struct RunArgs {
    /// State directory (see `enroll`).
    #[arg(long)]
    pub state_dir: Option<PathBuf>,
    /// How to run tools such as OpenTofu.
    #[arg(long, value_enum, default_value_t = RuntimeKind::Podman)]
    pub tool_runtime: RuntimeKind,
    /// The `tofu` binary for `--tool-runtime local`.
    #[arg(long, default_value = "tofu")]
    pub tofu_binary: PathBuf,
    /// The podman or docker binary (default: from PATH).
    #[arg(long)]
    pub container_binary: Option<PathBuf>,
    /// Container network for tool containers (`--network`).
    #[arg(long)]
    pub container_network: Option<String>,
    /// Directory for attempt workspaces (default: <state>/work).
    #[arg(long)]
    pub work_dir: Option<PathBuf>,
}

#[derive(Debug, Args)]
pub struct StatusArgs {
    /// State directory (see `enroll`).
    #[arg(long)]
    pub state_dir: Option<PathBuf>,
}

#[derive(Debug, Subcommand)]
pub enum ManagedIdentityCommand {
    Prepare(ManagedPrepareArgs),
    Install(ManagedInstallArgs),
}
#[derive(Debug, Args)]
pub struct ManagedPrepareArgs {
    #[arg(long)]
    pub generation: String,
    #[arg(long)]
    pub state_dir: PathBuf,
}
#[derive(Debug, Args)]
pub struct ManagedInstallArgs {
    #[arg(long)]
    pub generation: String,
    #[arg(long)]
    pub state_dir: PathBuf,
    #[arg(long)]
    pub payload_base64: String,
}
