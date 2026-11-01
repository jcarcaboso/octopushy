//! Runs one action of the technitium executor from the command line, so that
//! tests/technitium-cluster can drive real servers with the code the agent runs.
//!
//! `technitium_action <action> <input.json>`, with the password in TECHNITIUM_PASSWORD. Prints
//! the outputs as JSON. A failure prints them too, puts its message on stderr, and exits 1;
//! an uncertain outcome exits 3.
use std::collections::HashMap;
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure, Secrets};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

#[tokio::main]
async fn main() {
    let mut args = std::env::args().skip(1);
    let (Some(action), Some(input)) = (args.next(), args.next()) else {
        eprintln!("usage: technitium_action <action> <input.json>");
        std::process::exit(2);
    };
    let password = std::env::var("TECHNITIUM_PASSWORD").expect("TECHNITIUM_PASSWORD is not set");
    let input_json = std::fs::read_to_string(input).expect("the input cannot be read");
    let workspace = std::env::temp_dir();
    let cfg = ExecutorConfig {
        runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
        provider_cache: workspace.join("unused"),
        poll_interval: Duration::from_secs(5),
        proxmox_timeout: Duration::from_secs(15),
    };
    let secrets = HashMap::from([("technitium.password".to_owned(), password.clone())]);
    let buffer = Arc::new(LogBuffer::default());
    let ctx = ExecContext {
        attempt_id: "test".into(),
        action,
        input_json,
        secrets: Secrets::new(secrets),
        files_tar_gz: vec![],
        artifacts: vec![],
        workspace,
        log: AttemptLogger::new(
            buffer.clone(),
            Arc::new(Masker::new([password.as_str()])),
            Arc::new(Notify::new()),
        ),
        cancel: CancellationToken::new(),
        snapshots: Default::default(),
    };
    let result = executors::run(&cfg, "technitium", &ctx).await;
    if let Some((_, lines)) = buffer.batch_from(0, usize::MAX) {
        for line in lines {
            eprintln!("{}", line.text);
        }
    }
    match result {
        Ok(success) => println!("{}", success.outputs),
        Err(Failure::Failed { message, outputs }) => {
            println!("{}", outputs.unwrap_or_default());
            eprintln!("{message}");
            std::process::exit(1);
        }
        Err(other) => {
            eprintln!("{}", other.message());
            std::process::exit(3);
        }
    }
}
