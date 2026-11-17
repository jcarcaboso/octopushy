//! A tool's image is pulled in a step of its own, tried again when the registry fails, with a
//! fake container runtime that records what it was asked.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;
use std::time::Duration;

use octopushy::attempts::logs::{AttemptLogger, LogBuffer};
use octopushy::executors::opentofu::archive;
use octopushy::executors::{self, ExecContext, ExecutorConfig, Failure};
use octopushy::masking::Masker;
use octopushy::tools::{RuntimeKind, ToolRuntime};
use serde_json::json;
use tokio::sync::Notify;
use tokio_util::sync::CancellationToken;

const IMAGE: &str = "ghcr.io/opentofu/opentofu:1.13.1";

/// `image inspect` says whether the image is present (`$REC/present`); `pull` fails as many times
/// as `$REC/pull-fails` says, then succeeds; `run` is recorded and fails.
const FAKE_RUNTIME: &str = r#"#!/bin/bash
REC="@REC@"
printf '%s\n' "$*" >> "$REC/calls.log"
case "$1" in
  image) [ -f "$REC/present" ] ;;
  pull)
    n=$(cat "$REC/pull-fails" 2>/dev/null || echo 0)
    if [ "$n" -gt 0 ]; then
      echo $((n - 1)) > "$REC/pull-fails"
      echo "Error: pinging container registry ghcr.io: 503 Service Unavailable" >&2
      exit 125
    fi
    echo "Writing manifest to image destination" >&2
    touch "$REC/present" ;;
  *) exit 1 ;;
esac
"#;

struct Fake {
    dir: tempfile::TempDir,
    rec: PathBuf,
    cfg: ExecutorConfig,
}

impl Fake {
    fn new(pull_fails: u32, retry_delay: Duration) -> Self {
        let dir = tempfile::tempdir().unwrap();
        let rec = dir.path().join("rec");
        std::fs::create_dir_all(&rec).unwrap();
        std::fs::write(rec.join("pull-fails"), pull_fails.to_string()).unwrap();
        let bin = dir.path().join("podman");
        std::fs::write(&bin, FAKE_RUNTIME.replace("@REC@", &rec.to_string_lossy())).unwrap();
        use std::os::unix::fs::PermissionsExt;
        std::fs::set_permissions(&bin, std::fs::Permissions::from_mode(0o755)).unwrap();
        let cfg = ExecutorConfig {
            runtime: ToolRuntime::new(RuntimeKind::Podman, Some(bin), "tofu".into(), None),
            provider_cache: dir.path().join("cache"),
            poll_interval: Duration::from_millis(10),
            proxmox_timeout: Duration::from_secs(1),
            retry_delay,
            server: Default::default(),
            nix: Default::default(),
        };
        Self { dir, rec, cfg }
    }

    fn calls(&self, verb: &str) -> usize {
        std::fs::read_to_string(self.rec.join("calls.log"))
            .unwrap_or_default()
            .lines()
            .filter(|l| l.split(' ').next() == Some(verb))
            .count()
    }

    fn context(&self, action: &str, input: serde_json::Value, cancel: CancellationToken) -> Ctx {
        let buffer = Arc::new(LogBuffer::default());
        let secrets = HashMap::from([(
            "proxmox.token".to_owned(),
            "octopushy@pve!agent=5ec2e7".to_owned(),
        )]);
        let masker = Arc::new(Masker::new(secrets.values().map(String::as_str)));
        let workspace = self.dir.path().join("work");
        std::fs::create_dir_all(&workspace).unwrap();
        octopushy::attempts::workspace::init(&workspace, "a", "opentofu", action, true).unwrap();
        octopushy::attempts::workspace::mark_running(&workspace).unwrap();
        let files = archive::pack(&[(
            "modules/opentofu/vm/main.tf",
            b"resource \"terraform_data\" \"a\" {}\n",
        )])
        .unwrap();
        Ctx {
            buffer: buffer.clone(),
            ctx: ExecContext {
                attempt_id: "a".into(),
                action: action.into(),
                input_json: input.to_string(),
                secrets: executors::Secrets::new(secrets),
                files_tar_gz: files,
                artifacts: vec![],
                workspace,
                log: AttemptLogger::new(buffer, masker, Arc::new(Notify::new())),
                cancel,
                snapshots: Default::default(),
            },
        }
    }
}

struct Ctx {
    buffer: Arc<LogBuffer>,
    ctx: ExecContext,
}

impl Ctx {
    fn lines(&self) -> Vec<String> {
        self.buffer
            .batch_from(0, usize::MAX)
            .map(|(_, l)| l.into_iter().map(|x| x.text).collect())
            .unwrap_or_default()
    }
}

fn far() -> tokio::time::Instant {
    tokio::time::Instant::now() + Duration::from_secs(600)
}

#[tokio::test]
async fn a_pull_that_fails_once_is_tried_again_and_the_log_says_why() {
    let fake = Fake::new(1, Duration::from_millis(1));
    let c = fake.context("preview", json!({}), CancellationToken::new());

    executors::pull_image(&fake.cfg, &c.ctx, IMAGE, far())
        .await
        .unwrap();

    assert_eq!(fake.calls("pull"), 2);
    let lines = c.lines();
    assert!(
        lines
            .iter()
            .any(|l| l.contains(&format!("pulling the image {IMAGE} failed"))
                && l.contains("503 Service Unavailable")
                && l.contains("try 2 of 3")),
        "{lines:#?}"
    );

    // Present now: not pulled again.
    executors::pull_image(&fake.cfg, &c.ctx, IMAGE, far())
        .await
        .unwrap();
    assert_eq!(fake.calls("pull"), 2);
}

#[tokio::test]
async fn a_pull_that_keeps_failing_fails_the_preview_as_a_pull_before_the_tool_runs() {
    let fake = Fake::new(3, Duration::from_millis(1));
    let input = json!({
        "root": "modules/opentofu/vm",
        "image": IMAGE,
        "env": {},
        "secretEnv": {},
    });
    let c = fake.context("preview", input, CancellationToken::new());

    let r = executors::run(&fake.cfg, "opentofu", &c.ctx).await;

    assert!(
        matches!(&r, Err(Failure::Failed { message, .. })
            if message.starts_with(&format!("pulling the image {IMAGE} failed 3 times"))),
        "{r:?} {:#?}",
        c.lines()
    );
    assert_eq!(fake.calls("pull"), 3);
    assert_eq!(fake.calls("run"), 0, "no tool ran");
}

#[tokio::test]
async fn a_cancel_ends_the_wait_before_a_pull_is_tried_again() {
    let fake = Fake::new(1, Duration::from_secs(600));
    let cancel = CancellationToken::new();
    let c = fake.context("preview", json!({}), cancel.clone());
    tokio::spawn(async move {
        tokio::time::sleep(Duration::from_millis(100)).await;
        cancel.cancel();
    });
    let started = std::time::Instant::now();

    let r = executors::pull_image(&fake.cfg, &c.ctx, IMAGE, far()).await;

    assert!(matches!(r, Err(Failure::Cancelled)), "{r:?}");
    assert!(started.elapsed() < Duration::from_secs(30));
    assert_eq!(fake.calls("pull"), 1);
}
