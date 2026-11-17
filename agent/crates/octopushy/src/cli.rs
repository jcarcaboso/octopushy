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
    /// Operate a controller using a separately granted operator credential.
    Operator {
        #[arg(long, global = true)]
        config: Option<PathBuf>,
        #[command(subcommand)]
        command: OperatorCommand,
    },
    /// Run or manage this machine's agent.
    Agent {
        #[command(subcommand)]
        command: AgentCommand,
    },
    /// Print the version.
    Version,
}

#[derive(Debug, Subcommand)]
pub enum OperatorCommand {
    /// Save a console-created token from stdin. Never accepts a password.
    Login {
        /// Controller origin, http:// or https://. Over http:// the token is unencrypted.
        #[arg(long)]
        server: String,
        #[arg(long, required = true)]
        token_stdin: bool,
        /// Private CA that verifies an https:// controller. Refused with an http:// origin.
        #[arg(long)]
        ca_file: Option<PathBuf>,
        #[arg(long)]
        replace: bool,
    },
    /// Show the current authenticated operator.
    Whoami,
    /// Revoke this grant on the controller, then remove its local copy.
    Logout,
    /// Call the existing API. JSON bodies are read only from stdin.
    Request {
        path: String,
        #[arg(long, default_value = "GET", value_parser = ["GET", "POST", "PUT", "PATCH", "DELETE"])]
        method: String,
        #[arg(long)]
        json_stdin: bool,
    },
}

#[derive(Debug, Subcommand)]
pub enum AgentCommand {
    /// Enroll with a server using a one-time token.
    Enroll(EnrollArgs),
    /// Connect to the server and execute assignments.
    Run(RunArgs),
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
    /// The `octopushy-ansible` program for `--tool-runtime local`.
    #[arg(long, default_value = "octopushy-ansible")]
    pub ansible_binary: PathBuf,
    /// The `nix` binary for `--tool-runtime local`. Builds need it; other runtimes never build.
    #[arg(long, default_value = "nix")]
    pub nix_binary: PathBuf,
    /// Free space, in GiB, the Nix store's file system needs for this agent to take builds.
    #[arg(long, env = "OCTOPUSHY_NIX_MIN_FREE_GB", default_value_t = 20)]
    pub nix_min_free_gb: u64,
    /// Above this size, in GiB, the Nix store is garbage-collected after a build.
    #[arg(long, env = "OCTOPUSHY_NIX_STORE_MAX_GB", default_value_t = 40)]
    pub nix_store_max_gb: u64,
    /// The podman or docker binary (default: from PATH).
    #[arg(long)]
    pub container_binary: Option<PathBuf>,
    /// Container network for tool containers (`--network`).
    #[arg(long)]
    pub container_network: Option<String>,
    /// Most processes a tool container may run (`--pids-limit`); 0 leaves the runtime's default.
    #[arg(long, env = "OCTOPUSHY_CONTAINER_PIDS_LIMIT", default_value_t = crate::tools::DEFAULT_PIDS_LIMIT)]
    pub container_pids_limit: u32,
    /// Most memory a tool container may use, such as `4g` (`--memory`, swap included). No
    /// limit by default: a tool killed for memory midway through a change leaves it uncertain.
    #[arg(long, env = "OCTOPUSHY_CONTAINER_MEMORY", value_parser = parse_memory)]
    pub container_memory: Option<String>,
    /// Refuse a tool image not named by digest (`image:tag@sha256:<64 hex>`).
    #[arg(long, env = "OCTOPUSHY_REQUIRE_IMAGE_DIGEST", action = clap::ArgAction::SetTrue,
          value_parser = clap::builder::BoolishValueParser::new())]
    pub require_image_digest: bool,
    /// Directory for attempt workspaces (default: <state>/work).
    #[arg(long)]
    pub work_dir: Option<PathBuf>,
}

/// A size as the runtimes take it: digits and an optional unit, `b`, `k`, `m` or `g`.
fn parse_memory(s: &str) -> Result<String, String> {
    let digits = s.trim_end_matches(['b', 'k', 'm', 'g', 'B', 'K', 'M', 'G']);
    let unit = s.len() - digits.len();
    if digits.is_empty() || unit > 1 || !digits.bytes().all(|b| b.is_ascii_digit()) {
        return Err("expected a size such as 4g, 512m or 1073741824".into());
    }
    if digits.bytes().all(|b| b == b'0') {
        return Err("a memory limit must be more than zero".into());
    }
    Ok(s.to_ascii_lowercase())
}

#[derive(Debug, Args)]
pub struct StatusArgs {
    /// State directory (see `enroll`).
    #[arg(long)]
    pub state_dir: Option<PathBuf>,
}

#[cfg(test)]
mod tests {
    use super::*;
    use clap::Parser;

    fn run_args(extra: &[&str]) -> RunArgs {
        let args = ["octopushy", "agent", "run"].iter().chain(extra);
        match Cli::try_parse_from(args).unwrap().command {
            Command::Agent {
                command: AgentCommand::Run(args),
            } => args,
            other => panic!("{other:?}"),
        }
    }

    #[test]
    fn container_limits_and_digests() {
        let defaults = run_args(&[]);
        assert_eq!(defaults.container_pids_limit, 4096);
        assert_eq!(
            defaults.container_memory, None,
            "no memory limit by default"
        );
        assert!(!defaults.require_image_digest);
        let set = run_args(&[
            "--container-pids-limit",
            "0",
            "--container-memory",
            "4G",
            "--require-image-digest",
        ]);
        assert_eq!(set.container_pids_limit, 0);
        assert_eq!(set.container_memory.as_deref(), Some("4g"));
        assert!(set.require_image_digest);
        for good in ["512m", "1073741824", "2g"] {
            assert!(parse_memory(good).is_ok(), "{good}");
        }
        for bad in ["", "g", "0", "0m", "4gb", "-1g", "4 g", "1e9"] {
            assert!(parse_memory(bad).is_err(), "{bad}");
        }
    }
}
