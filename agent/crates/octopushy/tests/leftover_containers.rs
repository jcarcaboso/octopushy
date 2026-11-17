//! At startup the agent removes the containers a previous process of its own left, and no
//! other agent's, with a fake container runtime that keeps its containers in a file.

use std::os::unix::fs::PermissionsExt;
use std::path::PathBuf;

use octopushy::tools::{RuntimeKind, ToolRuntime};

/// `$REC/containers` holds `id agent attempt` lines (`-` for a missing label). `ps` lists every
/// ID whatever its filter says, so the agent's own check of the label is what keeps the others.
const FAKE_RUNTIME: &str = r#"#!/bin/bash
REC="@REC@"
printf '%s\n' "$*" >> "$REC/calls.log"
case "$1" in
  ps) cut -d' ' -f1 "$REC/containers" ;;
  inspect)
    shift 3
    for id in "$@"; do
      grep "^$id " "$REC/containers" | sed 's/ - / /; s/ -$//'
    done ;;
  rm)
    grep -v "^$4 " "$REC/containers" > "$REC/next"
    mv "$REC/next" "$REC/containers" ;;
  *) exit 1 ;;
esac
"#;

fn fake(containers: &str) -> (tempfile::TempDir, PathBuf, ToolRuntime) {
    let dir = tempfile::tempdir().unwrap();
    let rec = dir.path().join("rec");
    std::fs::create_dir_all(&rec).unwrap();
    std::fs::write(rec.join("containers"), containers).unwrap();
    let bin = dir.path().join("podman");
    std::fs::write(&bin, FAKE_RUNTIME.replace("@REC@", &rec.to_string_lossy())).unwrap();
    std::fs::set_permissions(&bin, std::fs::Permissions::from_mode(0o755)).unwrap();
    let mut rt = ToolRuntime::new(RuntimeKind::Podman, Some(bin), "tofu".into(), None);
    rt.owner = Some("agent-a".into());
    (dir, rec, rt)
}

#[tokio::test]
async fn removes_only_this_agents_leftover_containers() {
    let (_dir, rec, rt) = fake(
        "c1 agent-a att-1\n\
         c2 agent-b att-2\n\
         c3 - att-3\n\
         c4 agent-a -\n\
         c5 agent-ab att-5\n",
    );
    assert_eq!(rt.remove_leftovers().await.unwrap(), 2);
    assert_eq!(
        std::fs::read_to_string(rec.join("containers")).unwrap(),
        "c2 agent-b att-2\nc3 - att-3\nc5 agent-ab att-5\n",
        "another agent's containers and unlabelled ones stay"
    );
    let calls = std::fs::read_to_string(rec.join("calls.log")).unwrap();
    assert!(
        calls.contains("ps -a -q --no-trunc --filter label=io.octopushy.agent=agent-a"),
        "{calls}"
    );
    assert!(calls.contains("rm -f -- c1\n"), "{calls}");
    assert!(calls.contains("rm -f -- c4\n"), "{calls}");

    // Nothing left: nothing more to do.
    assert_eq!(rt.remove_leftovers().await.unwrap(), 0);
}

#[tokio::test]
async fn removes_nothing_without_an_owner_or_with_the_local_runtime() {
    let (_dir, rec, mut rt) = fake("c1 agent-a att-1\n");
    rt.owner = None;
    assert_eq!(rt.remove_leftovers().await.unwrap(), 0);
    rt.owner = Some("agent-a".into());
    rt.kind = RuntimeKind::Local;
    assert_eq!(rt.remove_leftovers().await.unwrap(), 0);
    assert!(
        !rec.join("calls.log").exists(),
        "the runtime was never asked"
    );
}
