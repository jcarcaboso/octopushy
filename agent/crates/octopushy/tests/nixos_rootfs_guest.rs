//! The real nixos-rootfs program, exactly as the executor submits it, in a fake guest.
//!
//! The mock guest agent runs the submitted command with bash. Only the tools it calls are
//! replaced, by a stub answering from block devices described in a temporary directory, so
//! no test reads or changes a real device. The stub's `growpart.service` finds its target
//! as the NixOS unit does, through `/dev/disk/by-label/nixos`, so a missing guard shows up
//! as a resized wrong disk. A real disk is covered only by live testing; see
//! docs/guest-filesystem-growth.md.
use axum::{
    Json, Router,
    routing::{get, post},
};
use octopushy::{
    attempts::logs::{AttemptLogger, LogBuffer},
    executors::{self, ExecContext, ExecutorConfig, Failure, Secrets, Success},
    masking::Masker,
    tools::{RuntimeKind, ToolRuntime},
};
use serde_json::{Value, json};
use std::{
    collections::HashMap,
    fs,
    os::unix::fs::{PermissionsExt, symlink},
    path::{Path, PathBuf},
    sync::{Arc, Mutex},
    time::Duration,
};
use tokio::{process::Command, sync::Notify};
use tokio_util::sync::CancellationToken;

const GIB: u64 = 1024 * 1024 * 1024;
const BOOT_DISK: u64 = 8 * GIB;
const STATE_DISK: u64 = 50 * GIB;
const LARGER_BOOT_DISK: u64 = 10 * GIB;
const ROOT_UUID: &str = "f222513b-ded1-49fa-b591-20ce86a2fe7f";
const OTHER_UUID: &str = "0b1f2a57-7a6d-4e34-9c1e-2f3f7c0d9a11";
const ESP_START: u64 = 16384;
const ESP_SECTORS: u64 = 509952;
const ROOT_START: u64 = ESP_START + ESP_SECTORS;
/// The space growpart leaves for the backup GPT.
const GPT_TAIL: u64 = 17408;
const BLOCK_SIZE: u64 = 4096;

const VM_NAME: &str = "opy-worker-01";
const VM_ADDRESS: &str = "home/umbrel/opy-worker-01";

/// Answers as util-linux, e2fsprogs, coreutils and systemd do for the devices in `$GUEST`.
/// Anything else is recorded as unexpected, and every test fails on that.
const TOOLS: &str = r#"
set -u
tool=${0##*/}
printf '%s %s\n' "$tool" "$*" >>"$GUEST/calls"
value() { if [[ -f $GUEST/$1 ]]; then printf '%s\n' "$(<"$GUEST/$1")"; else echo; fi; }
attribute() { value "block/$1/$2"; }
device() {
    name=${1#/dev/}
    [[ -d $GUEST/block/$name ]] || { printf '%s: %s: not a block device\n' "$tool" "$1" >&2; exit 32; }
}
# As nixos/modules/system/boot/grow-partition.nix: follow the label, strip the digits.
grow_partition() {
    [[ ! -f $GUEST/growpart-stuck ]] || return 0
    target=$(value link/nixos)
    target=${target#/dev/}
    parent=${target%%[0-9]*}
    bytes=$(( $(attribute "$parent" bytes) - $(attribute "$target" start) * 512 - GPT_TAIL ))
    printf '%s\n' "$bytes" >"$GUEST/block/$target/bytes"
    printf '%s\n' "$(( bytes / 512 ))" >"$GUEST/block/$target/size"
}
# As systemd-growfs: whatever is mounted at /.
grow_filesystem() {
    [[ ! -f $GUEST/growfs-stuck ]] || return 0
    target=$(value mount/source)
    target=${target#/dev/}
    printf '%s\n' "$(( $(attribute "$target" bytes) / BLOCK_SIZE ))" >"$GUEST/block/$target/blocks"
    if [[ -f $GUEST/uuid-after-growth ]]; then
        value uuid-after-growth >"$GUEST/mount/uuid"
    fi
}
case "$tool $*" in
"findmnt -n -o SOURCE /") value mount/source ;;
"findmnt -n -o FSTYPE /") value mount/fstype ;;
"findmnt -n -o UUID /") value mount/uuid ;;
"id -u") value uid ;;
"stat -f -c %b %S /") printf '%s %s\n' "$(value mount/usable-blocks)" "$BLOCK_SIZE" ;;
"readlink -f /dev/disk/"*)
    if [[ -f $GUEST/link/${2##*/} ]]; then value "link/${2##*/}"; else printf '%s\n' "$2"; fi ;;
"readlink -f "*) printf '%s\n' "$2" ;;
"lsblk -dn -o "*) device "$4"; attribute "$name" "$3" ;;
"lsblk -nr -o NAME "*)
    device "$4"
    for path in "$GUEST"/block/*; do
        child=${path##*/}
        if [[ $child == "$name" || $(attribute "$child" PKNAME) == "$name" ]]; then
            printf '%s\n' "$child"
        fi
    done ;;
"lsblk -nrp -o NAME,LABEL")
    for path in "$GUEST"/block/*; do
        printf '/dev/%s %s\n' "${path##*/}" "$(attribute "${path##*/}" LABEL)"
    done ;;
"blockdev --getss "*) device "$2"; attribute "$name" ss ;;
"blockdev --getsize64 "*) device "$2"; attribute "$name" bytes ;;
"dumpe2fs -h "*)
    device "$2"
    printf 'Filesystem volume name:   %s\n' "$(attribute "$name" superblock-label)"
    printf 'Filesystem UUID:          %s\n' "$(attribute "$name" uuid)"
    printf 'Reserved block count:     1\n'
    printf 'Block count:              %s\n' "$(attribute "$name" blocks)"
    printf 'Block size:               %s\n' "$BLOCK_SIZE" ;;
"cat /sys/class/block/"*/start | "cat /sys/class/block/"*/size)
    [[ -f $GUEST/block/${1#/sys/class/block/} ]] || exit 1
    value "block/${1#/sys/class/block/}" ;;
"systemctl show "*) value "unit/$2/$4" ;;
"systemctl restart growpart.service") grow_partition ;;
"systemctl restart systemd-growfs-root.service") grow_filesystem ;;
*) printf '%s %s\n' "$tool" "$*" >>"$GUEST/unexpected"; exit 99 ;;
esac
"#;

fn on_path(tool: &str) -> PathBuf {
    std::env::split_paths(&std::env::var_os("PATH").unwrap())
        .map(|dir| dir.join(tool))
        .find(|path| path.is_file())
        .unwrap_or_else(|| panic!("{tool} is needed to run the guest program"))
}

/// A guest's block devices and mounts, as files the stub reads and changes.
struct Guest {
    dir: tempfile::TempDir,
    boot: String,
    state: String,
}

impl Guest {
    /// A boot disk already expanded to its size, like a booted worker's.
    fn expanded(boot: &str, state: &str, disk_bytes: u64) -> Arc<Self> {
        Self::with_root(boot, state, disk_bytes, disk_bytes)
    }

    /// A boot disk resized beyond the 8 GiB its root partition still fills.
    fn needing_growth(boot: &str, state: &str) -> Arc<Self> {
        Self::with_root(boot, state, LARGER_BOOT_DISK, BOOT_DISK)
    }

    fn with_root(boot: &str, state: &str, disk_bytes: u64, filled_bytes: u64) -> Arc<Self> {
        let guest = Self {
            dir: tempfile::tempdir().unwrap(),
            boot: boot.into(),
            state: state.into(),
        };
        let root_bytes = filled_bytes - ROOT_START * 512 - GPT_TAIL;
        guest.device(
            boot,
            &[
                ("TYPE", "disk"),
                ("PTTYPE", "gpt"),
                ("ss", "512"),
                ("bytes", &disk_bytes.to_string()),
            ],
        );
        guest.device(
            &format!("{boot}1"),
            &[
                ("TYPE", "part"),
                ("PKNAME", boot),
                ("PTTYPE", "gpt"),
                ("FSTYPE", "vfat"),
                ("LABEL", "ESP"),
                ("start", &ESP_START.to_string()),
                ("size", &ESP_SECTORS.to_string()),
            ],
        );
        guest.device(
            &format!("{boot}2"),
            &[
                ("TYPE", "part"),
                ("PKNAME", boot),
                ("PTTYPE", "gpt"),
                ("FSTYPE", "ext4"),
                ("LABEL", "nixos"),
                ("superblock-label", "nixos"),
                ("uuid", ROOT_UUID),
                ("start", &ROOT_START.to_string()),
                ("size", &(root_bytes / 512).to_string()),
                ("bytes", &root_bytes.to_string()),
                ("blocks", &(root_bytes / BLOCK_SIZE).to_string()),
            ],
        );
        // Like the worker state disk: a whole-disk filesystem, no partition table.
        guest.device(
            state,
            &[
                ("TYPE", "disk"),
                ("FSTYPE", "ext4"),
                ("LABEL", "opy-worker-state"),
                ("superblock-label", "opy-worker-state"),
                ("uuid", OTHER_UUID),
                ("ss", "512"),
                ("bytes", &STATE_DISK.to_string()),
                ("blocks", &(STATE_DISK / BLOCK_SIZE).to_string()),
            ],
        );
        guest.device("sr0", &[("TYPE", "rom"), ("LABEL", "cidata")]);
        guest.set("mount/source", format!("/dev/{boot}2"));
        guest.set("mount/fstype", "ext4");
        guest.set("mount/uuid", ROOT_UUID);
        guest.set("mount/usable-blocks", 1973915);
        guest.set("link/nixos", format!("/dev/{boot}2"));
        guest.set("link/opy-worker-state-v1", format!("/dev/{state}"));
        guest.set("uid", 0);
        guest.set("os-release", "ID=nixos\n");
        for unit in ["growpart.service", "systemd-growfs-root.service"] {
            guest.set(&format!("unit/{unit}/LoadState"), "loaded");
            guest.set(&format!("unit/{unit}/ActiveState"), "active");
        }
        guest.install_tools();
        Arc::new(guest)
    }

    fn device(&self, name: &str, attributes: &[(&str, &str)]) {
        for (attribute, value) in attributes {
            self.set(&format!("block/{name}/{attribute}"), value);
        }
    }

    fn set(&self, file: &str, value: impl ToString) {
        let path = self.path().join(file);
        fs::create_dir_all(path.parent().unwrap()).unwrap();
        fs::write(path, format!("{}\n", value.to_string())).unwrap();
    }

    fn read(&self, file: &str) -> String {
        fs::read_to_string(self.path().join(file)).unwrap_or_default()
    }

    fn path(&self) -> &Path {
        self.dir.path()
    }

    fn root(&self, attribute: &str) -> String {
        self.read(&format!("block/{}2/{attribute}", self.boot))
            .trim()
            .into()
    }

    fn install_tools(&self) {
        let bin = self.path().join("bin");
        fs::create_dir(&bin).unwrap();
        let stub = bin.join("stub");
        let bash = on_path("bash");
        let header = format!(
            "#!{}
GPT_TAIL={GPT_TAIL} BLOCK_SIZE={BLOCK_SIZE}",
            bash.display()
        );
        fs::write(&stub, format!("{header}{TOOLS}")).unwrap();
        fs::set_permissions(&stub, fs::Permissions::from_mode(0o755)).unwrap();
        for tool in [
            "findmnt",
            "lsblk",
            "blockdev",
            "dumpe2fs",
            "stat",
            "readlink",
            "cat",
            "id",
            "systemctl",
        ] {
            symlink(&stub, bin.join(tool)).unwrap();
        }
        symlink(on_path("awk"), bin.join("awk")).unwrap();
    }

    /// The program with only its fixed NixOS PATH and os-release pointed at this guest.
    fn confine(&self, program: &str) -> String {
        let mut confined = program.to_string();
        for (fixed, replacement) in [
            (
                "export PATH=/run/current-system/sw/bin\n",
                format!("export PATH={}/bin\n", self.path().display()),
            ),
            (
                ". /etc/os-release\n",
                format!(". {}/os-release\n", self.path().display()),
            ),
        ] {
            assert_eq!(confined.matches(fixed).count(), 1, "{fixed}");
            confined = confined.replace(fixed, &replacement);
        }
        confined
    }

    /// What the guest agent does with the submitted command, without the host's lock.
    async fn execute(&self, command: &Value) -> Value {
        let command: Vec<String> = serde_json::from_value(command.clone()).unwrap();
        assert_eq!(
            command[..7],
            [
                "/run/current-system/sw/bin/flock",
                "-n",
                "-E",
                "75",
                "/run/lock/octopushy-nixos.lock",
                "/run/current-system/sw/bin/bash",
                "-c",
            ]
        );
        let output = Command::new(on_path("bash"))
            .arg("-c")
            .arg(self.confine(&command[7]))
            .args(&command[8..])
            .env_clear()
            .env("GUEST", self.path())
            // On a socket, bash would read the host's ~/.bashrc as if started by sshd.
            .stdin(std::process::Stdio::null())
            .output()
            .await
            .unwrap();
        json!({
            "exited": 1,
            "exitcode": output.status.code(),
            "out-data": String::from_utf8(output.stdout).unwrap(),
            "err-data": String::from_utf8(output.stderr).unwrap(),
        })
    }

    fn state_disk(&self) -> Vec<(PathBuf, String)> {
        let mut attributes: Vec<_> = fs::read_dir(self.path().join("block").join(&self.state))
            .unwrap()
            .map(|entry| {
                let path = entry.unwrap().path();
                let value = fs::read_to_string(&path).unwrap();
                (path, value)
            })
            .collect();
        attributes.sort();
        attributes
    }

    /// One executor run through the mock guest agent. In every test, however it ends, the
    /// program stays within the stubbed tools and the state disk is neither measured nor changed.
    async fn run(self: &Arc<Self>, action: &str, disk_bytes: u64) -> Result<Success, Failure> {
        let state_disk = self.state_disk();
        let status = Arc::new(Mutex::new(Value::Null));
        let finished = status.clone();
        let guest = self.clone();
        let app = Router::new()
            .route(
                "/api2/json/nodes/umbrel/qemu/9210/config",
                get(|| async {
                    Json(json!({"data":{
                        "name": VM_NAME,
                        "tags": "octopushy",
                        "description": format!("Managed by Octopushy as {VM_ADDRESS}."),
                    }}))
                }),
            )
            .route(
                "/api2/json/nodes/umbrel/qemu/9210/agent/exec",
                post(move |Json(body): Json<Value>| async move {
                    let result = guest.execute(&body["command"]).await;
                    *finished.lock().unwrap() = result;
                    Json(json!({"data":{"pid":42}}))
                }),
            )
            .route(
                "/api2/json/nodes/umbrel/qemu/9210/agent/exec-status",
                get(move || {
                    let status = status.lock().unwrap().clone();
                    async move { Json(json!({"data":status})) }
                }),
            );
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let address = listener.local_addr().unwrap();
        let server = tokio::spawn(async move { axum::serve(listener, app).await.unwrap() });
        let work = tempfile::tempdir().unwrap();
        let cfg = ExecutorConfig {
            runtime: ToolRuntime::new(RuntimeKind::Local, None, "false".into(), None),
            provider_cache: work.path().join("cache"),
            poll_interval: Duration::from_millis(5),
            proxmox_timeout: Duration::from_secs(30),
        };
        let ctx = ExecContext {
            attempt_id: "rootfs-guest-test".into(),
            action: action.into(),
            input_json: json!({
                "connection": {"endpoint": format!("http://{address}"), "verifyTls": true},
                "node": "umbrel",
                "vmid": 9210,
                "name": VM_NAME,
                "address": VM_ADDRESS,
                "diskBytes": disk_bytes,
                "waitSeconds": 1,
            })
            .to_string(),
            secrets: Secrets::new(HashMap::from([(
                "proxmox.token".into(),
                "test@pve!test=secret".into(),
            )])),
            files_tar_gz: vec![],
            artifacts: vec![],
            workspace: work.path().into(),
            log: AttemptLogger::new(
                Arc::new(LogBuffer::default()),
                Arc::new(Masker::new(["secret"])),
                Arc::new(Notify::new()),
            ),
            cancel: CancellationToken::new(),
            snapshots: Default::default(),
        };
        let result = executors::run(&cfg, "nixos-rootfs", &ctx).await;
        server.abort();

        assert_eq!(self.read("unexpected"), "", "the stub does not know this");
        assert_eq!(self.state_disk(), state_disk, "the state disk changed");
        let state_device = format!("/dev/{}", self.state);
        assert!(
            !self
                .read("calls")
                .lines()
                .filter(|call| !call.starts_with("readlink "))
                .any(|call| call.split(' ').any(|word| word == state_device)),
            "the state disk was measured:\n{}",
            self.read("calls")
        );
        result
    }

    fn restarted_units(&self) -> Vec<String> {
        self.read("calls")
            .lines()
            .filter_map(|call| call.strip_prefix("systemctl restart "))
            .map(String::from)
            .collect()
    }
}

fn known_failure(result: Result<Success, Failure>) -> String {
    match result {
        Err(Failure::Failed { message, .. }) => message,
        other => panic!("expected a known failure, got {other:?}"),
    }
}

const GROWTH_UNITS: [&str; 2] = ["growpart.service", "systemd-growfs-root.service"];

#[tokio::test]
async fn grows_and_verifies_the_mounted_root_whichever_disk_the_kernel_named_first() {
    for (boot, state) in [("sda", "sdb"), ("sdb", "sda"), ("sdaa", "sda")] {
        let guest = Guest::needing_growth(boot, state);

        let grown = guest.run("grow", LARGER_BOOT_DISK).await.unwrap();

        assert_eq!(guest.restarted_units(), GROWTH_UNITS, "{boot}");
        let partition = LARGER_BOOT_DISK - ROOT_START * 512 - GPT_TAIL;
        assert_eq!(guest.root("bytes"), partition.to_string());
        assert_eq!(grown.outputs["changed"], true);
        assert_eq!(grown.outputs["diskBytes"], LARGER_BOOT_DISK);
        assert_eq!(grown.outputs["partitionBytes"], partition);
        assert_eq!(grown.outputs["uuid"], ROOT_UUID);

        let verified = guest.run("verify", LARGER_BOOT_DISK).await.unwrap();

        assert_eq!(verified.outputs["changed"], false);
        assert_eq!(verified.outputs["partitionBytes"], partition);
        assert_eq!(guest.restarted_units().len(), 2, "verify changed nothing");
    }
}

/// The failed run: Linux named the 50 GiB state disk sda and the 8 GiB boot disk sdb, and
/// the native units had already expanded root during boot.
#[tokio::test]
async fn an_expanded_root_on_sdb_beside_the_state_disk_needs_no_change() {
    for (boot, state) in [("sdb", "sda"), ("sda", "sdb")] {
        let guest = Guest::expanded(boot, state, BOOT_DISK);
        for action in ["grow", "verify"] {
            let report = guest.run(action, BOOT_DISK).await.unwrap().outputs;

            assert_eq!(report["changed"], false, "{action} on {boot}");
            assert_eq!(report["diskBytes"], BOOT_DISK);
            assert_eq!(report["uuid"], ROOT_UUID);
        }
        assert!(guest.restarted_units().is_empty());
    }
}

#[tokio::test]
async fn verification_reports_room_to_grow_and_never_grows() {
    let guest = Guest::needing_growth("sdb", "sda");
    let before = guest.root("bytes");

    let message = known_failure(guest.run("verify", LARGER_BOOT_DISK).await);

    assert!(
        message.contains("Root partition has not expanded"),
        "{message}"
    );
    assert!(guest.restarted_units().is_empty());
    assert_eq!(guest.root("bytes"), before);
}

#[tokio::test]
async fn a_filesystem_behind_its_partition_is_grown_and_otherwise_refused() {
    let lagging = || {
        let guest = Guest::expanded("sdb", "sda", BOOT_DISK);
        let blocks: u64 = guest.root("blocks").parse().unwrap();
        guest.set("block/sdb2/blocks", blocks - 1);
        guest
    };

    let guest = lagging();
    let message = known_failure(guest.run("verify", BOOT_DISK).await);
    assert!(
        message.contains("Root filesystem has not expanded"),
        "{message}"
    );
    assert!(guest.restarted_units().is_empty());

    let guest = lagging();
    assert_eq!(
        guest.run("grow", BOOT_DISK).await.unwrap().outputs["changed"],
        true
    );
    assert_eq!(guest.restarted_units(), GROWTH_UNITS);
}

#[tokio::test]
async fn resolves_a_mount_source_given_through_a_link() {
    let guest = Guest::expanded("sdb", "sda", BOOT_DISK);
    guest.set("mount/source", "/dev/disk/by-label/nixos");

    guest.run("verify", BOOT_DISK).await.unwrap();
}

/// Each change is a file in the fake guest. Every case starts with room to grow, so a
/// missing guard would reach the native units and fail the no-restart assertion.
#[tokio::test]
async fn refuses_unsupported_roots_before_any_change() {
    let not_a_root_device = "Unsupported root device";
    let label = "Unexpected root label";
    let identity = "Root partition is not the mounted filesystem";
    let layout = "Unexpected disk layout";
    let offset = "Unexpected partition offset";
    let geometry = "Inconsistent disk geometry";
    let cases: &[(&str, &str, &str)] = &[
        // Root that is not partition 2 of a SCSI disk.
        ("mount/source", "/dev/dm-0", not_a_root_device),
        ("mount/source", "/dev/mapper/crypt-root", not_a_root_device),
        ("mount/source", "/dev/vda2", not_a_root_device),
        ("mount/source", "/dev/nvme0n1p2", not_a_root_device),
        ("mount/source", "/dev/sdb", not_a_root_device),
        ("mount/source", "/dev/sda", not_a_root_device),
        ("mount/source", "/dev/sdb1", not_a_root_device),
        ("mount/source", "/dev/sdb3", not_a_root_device),
        ("mount/source", "/dev/sdb2[/subvolume]", not_a_root_device),
        ("mount/source", "", not_a_root_device),
        ("mount/fstype", "btrfs", "Root is not ext4"),
        ("os-release", "ID=fedora", "Only NixOS is supported"),
        ("uid", "1000", "Guest-agent root access is required"),
        // Partition relationships the name alone does not prove.
        ("block/sdb2/TYPE", "disk", not_a_root_device),
        ("block/sdb2/PKNAME", "sda", not_a_root_device),
        ("block/sdb/TYPE", "mpath", not_a_root_device),
        // growpart.service would resize whatever the label names.
        ("link/nixos", "/dev/sda", label),
        ("link/nixos", "/dev/sdb1", label),
        ("link/nixos", "/dev/disk/by-label/nixos", label),
        // A clone or stale copy elsewhere makes the label ambiguous.
        ("block/sda/LABEL", "nixos", label),
        ("block/sr0/LABEL", "nixos", label),
        ("block/sdb2/LABEL", "other", label),
        // Stale udev data must not outvote the superblock.
        ("block/sdb2/superblock-label", "other", label),
        ("block/sdb2/uuid", OTHER_UUID, identity),
        ("mount/uuid", OTHER_UUID, identity),
        ("mount/uuid", "", "Missing root filesystem identity"),
        (
            "link/opy-worker-state-v1",
            "/dev/sdb",
            "The root disk is the worker state disk",
        ),
        ("block/sdb/PTTYPE", "dos", "Only GPT is supported"),
        ("block/sdb1/PKNAME", "sda", layout),
        ("block/sdb1/FSTYPE", "ext4", "Unexpected EFI partition"),
        ("block/sdb/ss", "4096", "Unsupported sector size"),
        ("block/sdb2/start", "0", offset),
        ("block/sdb2/start", "2097153", offset),
        ("block/sdb1/start", "526337", offset),
        ("block/sdb1/size", "1048576", offset),
        ("block/sdb2/bytes", "21474836480", geometry),
        ("block/sdb2/blocks", "5242880", geometry),
        ("block/sdb2/blocks", "", "Cannot read ext4 capacity"),
    ];
    for action in ["grow", "verify"] {
        for (file, value, expected) in cases {
            let guest = Guest::needing_growth("sdb", "sda");
            guest.set(file, value);

            let message = known_failure(guest.run(action, LARGER_BOOT_DISK).await);

            let case = format!("{action} with {file}={value}: {message}");
            let prefix = format!("Guest root {action} failed (exit Some(1)): ");
            assert_eq!(message, format!("{prefix}{expected}"), "{case}");
            assert!(guest.restarted_units().is_empty(), "{case}");
        }
    }
}

#[tokio::test]
async fn refuses_any_other_partition_count() {
    let extra = Guest::needing_growth("sdb", "sda");
    extra.device("sdb3", &[("TYPE", "part"), ("PKNAME", "sdb")]);
    let no_esp = Guest::needing_growth("sdb", "sda");
    fs::remove_dir_all(no_esp.path().join("block/sdb1")).unwrap();

    for guest in [extra, no_esp] {
        let message = known_failure(guest.run("grow", LARGER_BOOT_DISK).await);

        assert!(message.contains("Unexpected disk layout"), "{message}");
        assert!(guest.restarted_units().is_empty());
    }
}

#[tokio::test]
async fn the_approved_capacity_is_the_root_disks_and_never_the_state_disks() {
    for (boot, state) in [("sdb", "sda"), ("sda", "sdb")] {
        for action in ["grow", "verify"] {
            for approved in [STATE_DISK, 9 * GIB, 7 * GIB] {
                let guest = Guest::expanded(boot, state, BOOT_DISK);

                let message = known_failure(guest.run(action, approved).await);

                assert!(
                    message.contains("does not match the approved desired disk"),
                    "{message}"
                );
                assert!(guest.restarted_units().is_empty());
            }
        }
    }
}

#[tokio::test]
async fn refuses_native_units_that_are_missing_or_already_changing() {
    let unit = "unit/growpart.service";
    for (file, value, expected) in [
        ("LoadState", "not-found", "is not loaded"),
        ("ActiveState", "activating", "already changing state"),
        ("ActiveState", "deactivating", "already changing state"),
    ] {
        let guest = Guest::needing_growth("sdb", "sda");
        guest.set(&format!("{unit}/{file}"), value);

        let message = known_failure(guest.run("grow", LARGER_BOOT_DISK).await);

        assert!(message.contains(expected), "{message}");
        assert!(guest.restarted_units().is_empty());
    }
}

#[tokio::test]
async fn growth_that_changes_nothing_or_replaces_the_filesystem_fails() {
    for (fault, value, expected) in [
        ("growpart-stuck", "1", "Root partition has not expanded"),
        ("growfs-stuck", "1", "Root filesystem has not expanded"),
        (
            "uuid-after-growth",
            OTHER_UUID,
            "Root filesystem identity changed",
        ),
    ] {
        let guest = Guest::needing_growth("sdb", "sda");
        guest.set(fault, value);

        let message = known_failure(guest.run("grow", LARGER_BOOT_DISK).await);

        assert!(message.contains(expected), "{fault}: {message}");
        assert_eq!(guest.restarted_units(), GROWTH_UNITS);
    }
}
