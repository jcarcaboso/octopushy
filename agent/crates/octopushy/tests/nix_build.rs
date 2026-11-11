//! The `nix` executor end to end over real mutual TLS, with a fake `nix` that records its
//! arguments and environment and "builds" an image prepared by the test: Hello advertises it
//! only when it can build, a build's image arrives at the server in 4 MiB chunks, and its
//! outputs name it.

mod common;

use std::collections::HashMap;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};

use common::{CHUNK, Event, FakeServer, Options};
use octopushy::enrollment::{EnrollOptions, EnrollmentToken, enroll};
use octopushy::executors::opentofu::archive;
use octopushy::run::{RunOptions, run};
use octopushy::tools::RuntimeKind;
use octopushy_proto::v1::{
    Assignment, Outcome, agent_message::Kind as AgentKind, server_message::Kind as ServerKind,
};
use serde_json::{Value, json};
use sha2::{Digest, Sha256};
use tokio_util::sync::CancellationToken;

const FAKE_NIX: &str = r#"#!/bin/bash
set -u
REC="@REC@"
case "$1" in
  --version) echo "nix (Nix) 2.34.0" ;;
  build)
    printf '%s\n' "$*" >> "$REC/args.log"
    env | sort > "$REC/env.log"
    installable="${@: -1}"
    dir="${installable#path:}"; dir="${dir%%#*}"
    cp "$dir/flake.lock" "$REC/flake.lock"
    case "$installable" in
      *.broken) echo "error: flake does not provide attribute 'broken'" >&2; exit 1 ;;
    esac
    echo "building '/nix/store/aaaa-image.drv'..." >&2
    echo "@OUT@" ;;
  store) printf '%s\n' "$*" >> "$REC/gc.log" ;;
  *) echo "unknown command $1" >&2; exit 1 ;;
esac
"#;

struct Setup {
    _dir: tempfile::TempDir,
    rec: PathBuf,
    nix: PathBuf,
    store: PathBuf,
    out: PathBuf,
}

fn setup(image: &[u8]) -> Setup {
    let dir = tempfile::tempdir().unwrap();
    let root = dir.path().canonicalize().unwrap();
    let rec = root.join("rec");
    let store = root.join("nixroot");
    let out = store.join("store/abc-nixos-base-image");
    std::fs::create_dir_all(&rec).unwrap();
    std::fs::create_dir_all(out.join("nix-support")).unwrap();
    std::fs::write(out.join("nixos.raw"), image).unwrap();
    let nix = root.join("bin/nix");
    std::fs::create_dir_all(nix.parent().unwrap()).unwrap();
    std::fs::write(
        &nix,
        FAKE_NIX
            .replace("@REC@", &rec.to_string_lossy())
            .replace("@OUT@", &out.to_string_lossy()),
    )
    .unwrap();
    std::fs::set_permissions(&nix, std::fs::Permissions::from_mode(0o755)).unwrap();
    Setup {
        _dir: dir,
        rec,
        nix,
        store,
        out,
    }
}

async fn enroll_into(server: &FakeServer, dir: &Path) {
    enroll(EnrollOptions {
        server: server.url.clone(),
        token: EnrollmentToken::parse(&server.token()).unwrap(),
        name: Some("test-agent".into()),
        state_dir: dir.to_path_buf(),
        force: false,
    })
    .await
    .unwrap();
}

fn options(s: &Setup, state: &Path) -> RunOptions {
    let mut opts = RunOptions::new(state.to_path_buf());
    opts.tool_runtime = RuntimeKind::Local;
    opts.tofu_binary = "/bin/false".into();
    opts.nix_binary = s.nix.clone();
    opts.nix_store_dir = s.store.clone();
    opts.nix_min_free_bytes = 0;
    opts
}

async fn hello_executors(server: &mut FakeServer) -> Vec<String> {
    server
        .wait_for(|event| match event {
            Event::Message(m) => match &m.kind {
                Some(AgentKind::Hello(h)) => Some(h.executors.clone()),
                _ => None,
            },
            _ => None,
        })
        .await
}

fn bundle() -> Vec<u8> {
    archive::pack(&[
        ("modules/nixos/flake.nix", b"{ outputs = _: { }; }\n"),
        ("modules/nixos/flake.lock", b"WORKFLOW LOCK\n"),
        ("templates/nixos-base/flake.lock", b"LAB LOCK\n"),
    ])
    .unwrap()
}

fn build_assignment(id: &str, output: &str, lock: Option<&str>) -> Assignment {
    let mut input = json!({
        "key": "ab".repeat(32),
        "flake": "modules/nixos",
        "output": output,
        "system": "x86_64-linux",
    });
    if let Some(lock) = lock {
        input["lock"] = json!(lock);
    }
    Assignment {
        attempt_id: id.into(),
        executor: "nix".into(),
        action: "build".into(),
        input_json: input.to_string(),
        files_tar_gz: bundle(),
        lease_seconds: 60,
        ..Default::default()
    }
}

fn env_of(rec: &Path) -> HashMap<String, String> {
    std::fs::read_to_string(rec.join("env.log"))
        .unwrap()
        .lines()
        .filter_map(|l| l.split_once('='))
        .map(|(k, v)| (k.to_owned(), v.to_owned()))
        .collect()
}

#[tokio::test]
async fn nix_is_advertised_only_when_it_can_build() {
    let s = setup(b"img");
    for (kind, nix, min_free, advertised) in [
        (RuntimeKind::Local, s.nix.clone(), 0, true),
        (RuntimeKind::Local, s.nix.clone(), u64::MAX, false),
        (
            RuntimeKind::Local,
            PathBuf::from("/nonexistent/nix"),
            0,
            false,
        ),
        (RuntimeKind::Podman, s.nix.clone(), 0, false),
    ] {
        let mut server = FakeServer::start(Options::default()).await;
        let state = tempfile::tempdir().unwrap();
        enroll_into(&server, state.path()).await;
        let mut opts = options(&s, state.path());
        opts.tool_runtime = kind;
        opts.container_binary = Some("/bin/false".into());
        opts.nix_binary = nix.clone();
        opts.nix_min_free_bytes = min_free;
        let shutdown = CancellationToken::new();
        let task = tokio::spawn(run(opts, shutdown.clone()));
        let executors = hello_executors(&mut server).await;
        assert_eq!(
            executors.contains(&"nix".to_owned()),
            advertised,
            "{kind:?} {nix:?} {min_free}: {executors:?}"
        );
        shutdown.cancel();
        task.await.unwrap().unwrap();
    }
}

#[tokio::test]
async fn a_build_sends_its_image_and_names_it() {
    common::init_tracing();
    // Two whole chunks and a bit.
    let image: Vec<u8> = (0..2 * CHUNK + 5).map(|i| (i % 251) as u8).collect();
    let sha256 = hex::encode(Sha256::digest(&image));
    let s = setup(&image);
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        ..Options::default()
    })
    .await;
    let state = tempfile::tempdir().unwrap();
    enroll_into(&server, state.path()).await;
    let state_dir = state.path().canonicalize().unwrap();
    let mut opts = options(&s, state.path());
    // Any store is above one byte: every build collects garbage.
    opts.nix_store_max_bytes = 1;
    let shutdown = CancellationToken::new();
    let task = tokio::spawn(run(opts, shutdown.clone()));
    assert_eq!(
        hello_executors(&mut server).await,
        ["proxmox", "nix"],
        "/bin/false is no tofu"
    );

    server
        .send(ServerKind::Assignment(build_assignment(
            "build-1",
            "base-image",
            Some("templates/nixos-base/flake.lock"),
        )))
        .await;
    // The server has the image before the agent reports the result.
    let (attempt_id, file_name, chunks) = server
        .wait_for(|e| match e {
            Event::ImageUploaded {
                attempt_id,
                file_name,
                chunks,
                ..
            } => Some((attempt_id.clone(), file_name.clone(), chunks.clone())),
            _ => None,
        })
        .await;
    assert_eq!(attempt_id, "build-1");
    assert_eq!(file_name, "nixos.raw");
    assert_eq!(chunks, [CHUNK, CHUNK, 5]);
    assert_eq!(server.image(&sha256).unwrap().data, image);
    let (result, _) = server.wait_result("build-1").await;
    assert_eq!(result.outcome(), Outcome::Succeeded, "{}", result.error);
    let out: Value = serde_json::from_str(&result.outputs_json).unwrap();
    assert_eq!(
        out,
        json!({
            "sha256": sha256,
            "size": image.len(),
            "fileName": "nixos.raw",
            "outPath": s.out.to_string_lossy(),
        })
    );

    // The flake builds with the lab's lock, exactly as the contract writes the command.
    let ws = state_dir.join("work/build-1");
    assert_eq!(
        std::fs::read_to_string(s.rec.join("args.log")).unwrap(),
        format!(
            "build --no-update-lock-file --no-link --print-out-paths path:{}#packages.x86_64-linux.base-image\n",
            ws.join("repo/modules/nixos").display()
        )
    );
    assert_eq!(
        std::fs::read_to_string(s.rec.join("flake.lock")).unwrap(),
        "LAB LOCK\n"
    );
    let env = env_of(&s.rec);
    assert_eq!(Path::new(&env["TMPDIR"]), ws.join("tmp"));
    assert_eq!(Path::new(&env["HOME"]), ws.join("home"));
    assert_eq!(
        Path::new(&env["XDG_CACHE_HOME"]),
        state_dir.join("cache/nix")
    );
    // The store is above its limit, so the build collected garbage.
    let gc = std::fs::read_to_string(s.rec.join("gc.log")).unwrap();
    assert!(gc.starts_with("store gc --max "), "{gc}");

    // Without a lock, the workflow's own; a failed build says why.
    std::fs::remove_file(s.rec.join("gc.log")).unwrap();
    server
        .send(ServerKind::Assignment(build_assignment(
            "build-2", "broken", None,
        )))
        .await;
    let (result, _) = server.wait_result("build-2").await;
    assert_eq!(result.outcome(), Outcome::Failed);
    assert!(
        result.error.contains("nix build failed (exit status 1)")
            && result
                .error
                .contains("flake does not provide attribute 'broken'"),
        "{}",
        result.error
    );
    assert_eq!(
        std::fs::read_to_string(s.rec.join("flake.lock")).unwrap(),
        "WORKFLOW LOCK\n"
    );
    assert!(s.rec.join("gc.log").exists(), "a failed build collects too");

    shutdown.cancel();
    task.await.unwrap().unwrap();
}

#[tokio::test]
async fn a_build_fails_when_the_server_reports_another_image() {
    let image = vec![7u8; 1000];
    let s = setup(&image);
    let mut server = FakeServer::start(Options {
        auto_ack: true,
        misreport_upload: true,
        ..Options::default()
    })
    .await;
    let state = tempfile::tempdir().unwrap();
    enroll_into(&server, state.path()).await;
    let shutdown = CancellationToken::new();
    let task = tokio::spawn(run(options(&s, state.path()), shutdown.clone()));
    hello_executors(&mut server).await;
    server
        .send(ServerKind::Assignment(build_assignment(
            "build-3",
            "base-image",
            None,
        )))
        .await;
    let (result, _) = server.wait_result("build-3").await;
    assert_eq!(result.outcome(), Outcome::Failed);
    assert!(
        result
            .error
            .contains("the server received 1000 bytes with SHA-256 0000"),
        "{}",
        result.error
    );
    shutdown.cancel();
    task.await.unwrap().unwrap();
}
