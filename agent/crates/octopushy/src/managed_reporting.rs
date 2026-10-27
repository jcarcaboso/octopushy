//! Fixed image-native reporter configuration. Sensitive data is accepted only on
//! bounded stdin; neither the identity bootstrap protocol nor Nix inputs carry it.
use crate::{fsutil, managed_identity::valid_uuid};
use anyhow::{Result, ensure};
use serde::{Deserialize, Serialize};
use serde_json::json;
use std::{
    fs::{self, OpenOptions},
    io::Read,
    os::{
        fd::AsRawFd,
        unix::fs::{DirBuilderExt, MetadataExt, OpenOptionsExt, symlink},
    },
    path::Path,
    process::{Command, Stdio},
};

pub const INTERVAL_SECONDS: u32 = 3600;
pub(crate) const LIMIT: u64 = 24576;
const ROOT: &str = "/var/lib/octopushy-disk-reporter";

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub(crate) struct Configuration {
    pub protocol_version: u32,
    pub generation: String,
    pub endpoint: String,
    pub interval_seconds: u32,
    pub token: String,
    pub ca_certificate_pem: Option<String>,
}
impl Configuration {
    pub(crate) fn validate(&self) -> Result<()> {
        let url = url::Url::parse(&self.endpoint)
            .map_err(|_| anyhow::anyhow!("invalid reporting endpoint"))?;
        let parts: Vec<_> = url.path().split('/').collect();
        ensure!(
            self.protocol_version == 1
                && valid_uuid(&self.generation)
                && self.interval_seconds == INTERVAL_SECONDS
                && self.token.len() == 64
                && self.token.bytes().all(|b| b.is_ascii_hexdigit())
                && self.endpoint.len() <= 2048
                && url.scheme() == "https"
                && url.host_str().is_some()
                && url.username().is_empty()
                && url.password().is_none()
                && url.query().is_none()
                && url.fragment().is_none()
                && parts.len() == 5
                && parts[1] == "api"
                && parts[2] == "guests"
                && valid_uuid(parts[3])
                && parts[4] == "disk-usage",
            "invalid reporting configuration (native v1 requires a 3600s interval)"
        );
        if let Some(ca) = &self.ca_certificate_pem {
            ensure!(
                ca.len() <= 16384 && !ca.contains("PRIVATE KEY"),
                "invalid reporting CA"
            );
            reqwest::Certificate::from_pem(ca.as_bytes())
                .map_err(|_| anyhow::anyhow!("invalid reporting CA"))?;
        }
        Ok(())
    }
}

fn read(input: impl Read) -> Result<Configuration> {
    let mut bytes = Vec::new();
    input.take(LIMIT + 1).read_to_end(&mut bytes)?;
    ensure!(bytes.len() as u64 <= LIMIT, "reporting input too large");
    // Do not propagate serde diagnostics: unknown field names/values may be secrets.
    let config: Configuration =
        serde_json::from_slice(&bytes).map_err(|_| anyhow::anyhow!("invalid reporting input"))?;
    config.validate()?;
    Ok(config)
}

fn protected_dir(root: &Path) -> Result<()> {
    match fs::DirBuilder::new().mode(0o700).create(root) {
        Ok(()) => (),
        Err(e) if e.kind() == std::io::ErrorKind::AlreadyExists => (),
        Err(e) => return Err(e.into()),
    }
    let meta = fs::symlink_metadata(root)?;
    ensure!(
        meta.is_dir() && meta.uid() == unsafe { libc::geteuid() } && meta.mode() & 0o777 == 0o700,
        "reporting directory must be private and owned by the helper"
    );
    Ok(())
}

fn publish(root: &Path, config: &Configuration) -> Result<()> {
    // All three credentials switch as one directory; no reader can observe a
    // new endpoint with an old token. Only root can traverse the parent.
    let config_json = serde_json::to_vec(&json!({
        "endpoint": config.endpoint, "intervalSeconds": config.interval_seconds,
        "caCertificateFile": config.ca_certificate_pem.as_ref().map(|_| "/run/credentials/octopushy-disk-reporter.service/ca.pem"),
    }))?;
    let files = [
        ("config.json", config_json.as_slice()),
        ("token", config.token.as_bytes()),
        (
            "ca.pem",
            config
                .ca_certificate_pem
                .as_deref()
                .unwrap_or("")
                .as_bytes(),
        ),
    ];
    let current = root.join("current");
    let old = match fs::read_link(&current) {
        Ok(target) => {
            ensure!(
                target.components().count() == 1
                    && target
                        .to_str()
                        .is_some_and(|v| v.starts_with("configuration-")),
                "invalid credential link"
            );
            Some(target)
        }
        Err(e) if e.kind() == std::io::ErrorKind::NotFound => None,
        Err(e) => return Err(e.into()),
    };
    if old.is_some()
        && files
            .iter()
            .all(|(name, data)| fs::read(current.join(name)).is_ok_and(|old| old == *data))
    {
        return Ok(());
    }
    let name = format!("configuration-{:016x}", fastrand::u64(..));
    let stage = root.join(&name);
    fs::DirBuilder::new().mode(0o700).create(&stage)?;
    let link = root.join(format!(".current-{:016x}", fastrand::u64(..)));
    let result = (|| -> Result<()> {
        for (name, bytes) in files {
            fsutil::write_atomic(&stage.join(name), bytes, 0o600)?;
        }
        fsutil::sync_dir(&stage)?;
        symlink(&name, &link)?;
        fs::rename(&link, &current)?;
        fsutil::sync_dir(root)?;
        Ok(())
    })();
    if result.is_err() {
        let _ = fs::remove_file(link);
        if fs::read_link(&current).ok().as_deref() != Some(Path::new(&name)) {
            let _ = fs::remove_dir_all(stage);
        }
    }
    result?;
    if let Some(old) = old {
        fs::remove_dir_all(root.join(old))?;
        fsutil::sync_dir(root)?;
    }
    Ok(())
}

fn systemctl(args: &[&str]) -> Result<()> {
    ensure!(
        Command::new("/run/current-system/sw/bin/systemctl")
            .args(args)
            .env_clear()
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()?
            .success(),
        "reporter service operation failed; inspect its journal (no automatic replay)"
    );
    Ok(())
}

fn check_identity(input: impl Read, expected: &str, enrolled: bool) -> Result<()> {
    #[derive(Deserialize)]
    struct Generation {
        generation: String,
    }
    let generation: Generation = serde_json::from_reader(input.take(65536))
        .map_err(|_| anyhow::anyhow!("worker generation is unavailable"))?;
    ensure!(
        enrolled && generation.generation == expected,
        "worker generation/identity mismatch"
    );
    Ok(())
}

/// The only production entrypoint: no configurable path, command or unit name.
pub fn configure_stdin() -> Result<()> {
    ensure!(
        unsafe { libc::geteuid() } == 0,
        "reporting helper requires root"
    );
    let config = read(std::io::stdin().lock())?;
    check_identity(
        fs::File::open("/var/lib/octopushy/identity/managed-identity.json")?,
        &config.generation,
        Path::new("/var/lib/octopushy/identity/agent.json").is_file(),
    )?;
    let root = Path::new(ROOT);
    protected_dir(root)?;
    let lock = OpenOptions::new()
        .read(true)
        .write(true)
        .create(true)
        .truncate(false)
        .custom_flags(libc::O_NOFOLLOW)
        .mode(0o600)
        .open(root.join("lock"))?;
    ensure!(
        unsafe { libc::flock(lock.as_raw_fd(), libc::LOCK_EX | libc::LOCK_NB) } == 0,
        "reporter configuration is already running"
    );
    systemctl(&[
        "stop",
        "octopushy-disk-reporter.timer",
        "octopushy-disk-reporter.service",
    ])?;
    publish(root, &config)?;
    systemctl(&["reset-failed", "octopushy-disk-reporter.service"])?;
    // The oneshot is not RemainAfterExit. A successful start waits for a new
    // authenticated HTTPS upload and the reporter requires exactly HTTP 204.
    systemctl(&["start", "octopushy-disk-reporter.service"])?;
    systemctl(&["start", "octopushy-disk-reporter.timer"])?;
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::unix::fs::PermissionsExt;

    fn configuration() -> Configuration {
        Configuration {
            protocol_version: 1,
            generation: "884d09b4-0934-43a4-bffb-d2f7977bc01c".into(),
            endpoint: "https://web.test/api/guests/884d09b4-0934-43a4-bffb-d2f7977bc01c/disk-usage"
                .into(),
            interval_seconds: INTERVAL_SECONDS,
            token: "b".repeat(64),
            ca_certificate_pem: None,
        }
    }

    #[test]
    fn bounded_typed_input_never_echoes_secret_parser_values() {
        let valid = serde_json::to_vec(&configuration()).unwrap();
        assert!(read(valid.as_slice()).is_ok());
        for data in [
            vec![b' '; LIMIT as usize + 1],
            br#"{"PRIVATE-TOKEN-ENCODING":"hidden"}"#.to_vec(),
            b"not-json-secret".to_vec(),
        ] {
            let error = read(data.as_slice()).err().unwrap().to_string();
            assert!(
                !error.contains("PRIVATE-TOKEN-ENCODING") && !error.contains("not-json-secret")
            );
        }
        for endpoint in [
            "http://web.test",
            "https://user:password@web.test/api/guests/id/disk-usage",
            "https://web.test/api/guests/not-a-uuid/disk-usage",
        ] {
            let mut config = configuration();
            config.endpoint = endpoint.into();
            assert!(config.validate().is_err());
        }
        let mut config = configuration();
        config.interval_seconds = 1800;
        assert!(config.validate().is_err());
        config = configuration();
        config.ca_certificate_pem = Some("PRIVATE KEY".into());
        assert!(config.validate().is_err());
        config = configuration();
        config.generation = "wrong".into();
        assert!(config.validate().is_err());
    }

    #[test]
    fn local_generation_must_match_an_already_enrolled_identity_without_echoing_key_material() {
        let data = json!({"generation": configuration().generation, "keyPem": "synthetic-private-material"}).to_string();
        assert!(check_identity(data.as_bytes(), &configuration().generation, true).is_ok());
        for (expected, enrolled) in [
            (&configuration().generation[..], false),
            ("78cd5ef0-ef9d-4ad4-a227-bd49c52d21ab", true),
        ] {
            let error = check_identity(data.as_bytes(), expected, enrolled).unwrap_err();
            assert!(!error.to_string().contains("synthetic-private-material"));
        }
    }

    #[test]
    fn credential_bundle_is_private_atomic_and_idempotent() {
        let temp = tempfile::tempdir().unwrap();
        let root = temp.path().join("reporter");
        protected_dir(&root).unwrap();
        let mut config = configuration();
        publish(&root, &config).unwrap();
        let first = fs::read_link(root.join("current")).unwrap();
        publish(&root, &config).unwrap();
        assert_eq!(first, fs::read_link(root.join("current")).unwrap());
        assert_eq!(fs::metadata(&root).unwrap().mode() & 0o777, 0o700);
        for name in ["config.json", "token", "ca.pem"] {
            assert_eq!(
                fs::metadata(root.join("current").join(name))
                    .unwrap()
                    .mode()
                    & 0o777,
                0o600
            );
        }
        assert!(
            !fs::read_to_string(root.join("current/config.json"))
                .unwrap()
                .contains(&config.token)
        );
        config.token = "c".repeat(64);
        publish(&root, &config).unwrap();
        assert_ne!(first, fs::read_link(root.join("current")).unwrap());
        assert!(!root.join(first).exists());
        assert_eq!(
            fs::read_to_string(root.join("current/token")).unwrap(),
            config.token
        );
        assert_eq!(fs::read_dir(&root).unwrap().count(), 2);
        fs::set_permissions(&root, fs::Permissions::from_mode(0o755)).unwrap();
        assert!(protected_dir(&root).is_err());
        let link = temp.path().join("symlink");
        symlink(&root, &link).unwrap();
        assert!(protected_dir(&link).is_err());
    }
}
