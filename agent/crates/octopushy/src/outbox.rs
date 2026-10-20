//! The outbox: results written to disk before they are sent, deleted only when
//! the server acknowledges them, and resent after every (re)connect.
//!
//! Each result is `<outbox>/<attempt_id>.json`, written atomically. Artifacts are
//! stored inline as base64.

use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{Context, Result, bail};
use base64::Engine;
use octopushy_proto::v1::{Artifact, AttemptResult, Outcome};
use serde::{Deserialize, Serialize};

use crate::fsutil;
use crate::paths::is_safe_id;

/// One stored result.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct OutboxRecord {
    pub attempt_id: String,
    /// The proto enum name, such as `OUTCOME_SUCCEEDED`.
    pub outcome: String,
    pub outputs_json: String,
    pub error: String,
    #[serde(default)]
    pub artifacts: Vec<StoredArtifact>,
    pub created_at_ms: u64,
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct StoredArtifact {
    pub name: String,
    pub sha256: String,
    pub content_base64: String,
}

impl OutboxRecord {
    pub fn from_result(result: &AttemptResult, created_at_ms: u64) -> Self {
        let b64 = base64::engine::general_purpose::STANDARD;
        Self {
            attempt_id: result.attempt_id.clone(),
            outcome: result.outcome().as_str_name().to_owned(),
            outputs_json: result.outputs_json.clone(),
            error: result.error.clone(),
            artifacts: result
                .artifacts
                .iter()
                .map(|a| StoredArtifact {
                    name: a.name.clone(),
                    sha256: a.sha256.clone(),
                    content_base64: b64.encode(&a.content),
                })
                .collect(),
            created_at_ms,
        }
    }

    pub fn to_result(&self) -> Result<AttemptResult> {
        let b64 = base64::engine::general_purpose::STANDARD;
        let outcome = Outcome::from_str_name(&self.outcome)
            .with_context(|| format!("unknown outcome {:?}", self.outcome))?;
        let artifacts = self
            .artifacts
            .iter()
            .map(|a| {
                Ok(Artifact {
                    name: a.name.clone(),
                    content: b64.decode(&a.content_base64)?,
                    sha256: a.sha256.clone(),
                })
            })
            .collect::<Result<Vec<_>>>()?;
        Ok(AttemptResult {
            attempt_id: self.attempt_id.clone(),
            outcome: outcome as i32,
            outputs_json: self.outputs_json.clone(),
            error: self.error.clone(),
            artifacts,
        })
    }
}

/// The outbox directory.
#[derive(Debug, Clone)]
pub struct Outbox {
    dir: PathBuf,
}

impl Outbox {
    /// Opens (and creates) the outbox, removing temp files of interrupted writes.
    pub fn open(dir: impl Into<PathBuf>) -> Result<Self> {
        let dir = dir.into();
        fsutil::ensure_private_dir(&dir)?;
        for entry in fs::read_dir(&dir)? {
            let entry = entry?;
            if entry.file_name().to_string_lossy().starts_with('.') {
                let _ = fs::remove_file(entry.path());
            }
        }
        Ok(Self { dir })
    }

    pub fn dir(&self) -> &Path {
        &self.dir
    }

    fn path(&self, attempt_id: &str) -> Result<PathBuf> {
        if !is_safe_id(attempt_id) {
            bail!("unsafe attempt ID {attempt_id:?}");
        }
        Ok(self.dir.join(format!("{attempt_id}.json")))
    }

    /// Stores a result durably.
    pub fn put(&self, record: &OutboxRecord) -> Result<()> {
        let path = self.path(&record.attempt_id)?;
        let json = serde_json::to_vec(record)?;
        fsutil::write_atomic(&path, &json, 0o600)
            .with_context(|| format!("writing the result of {} to the outbox", record.attempt_id))
    }

    pub fn contains(&self, attempt_id: &str) -> bool {
        self.path(attempt_id).map(|p| p.exists()).unwrap_or(false)
    }

    /// Reads a stored result. A corrupt file is moved aside and reported as
    /// missing, so one bad file cannot block the others.
    pub fn get(&self, attempt_id: &str) -> Result<Option<OutboxRecord>> {
        let path = self.path(attempt_id)?;
        let bytes = match fs::read(&path) {
            Ok(b) => b,
            Err(e) if e.kind() == std::io::ErrorKind::NotFound => return Ok(None),
            Err(e) => return Err(e).with_context(|| format!("reading {}", path.display())),
        };
        match serde_json::from_slice::<OutboxRecord>(&bytes) {
            Ok(r) => Ok(Some(r)),
            Err(e) => {
                let aside = path.with_extension("json.corrupt");
                tracing::error!(
                    "outbox entry {} is corrupt ({e}); moving it to {}",
                    path.display(),
                    aside.display()
                );
                let _ = fs::rename(&path, &aside);
                Ok(None)
            }
        }
    }

    /// Deletes a result after the server acknowledged it.
    pub fn remove(&self, attempt_id: &str) -> Result<()> {
        fsutil::remove_durably(&self.path(attempt_id)?)
    }

    /// The attempt IDs in the outbox, oldest first.
    pub fn list(&self) -> Result<Vec<String>> {
        let mut entries = Vec::new();
        for entry in fs::read_dir(&self.dir)? {
            let entry = entry?;
            let name = entry.file_name().to_string_lossy().into_owned();
            let Some(id) = name.strip_suffix(".json") else {
                continue;
            };
            if !is_safe_id(id) {
                continue;
            }
            let modified = entry
                .metadata()
                .and_then(|m| m.modified())
                .unwrap_or(std::time::UNIX_EPOCH);
            entries.push((modified, id.to_owned()));
        }
        entries.sort();
        Ok(entries.into_iter().map(|(_, id)| id).collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn result(id: &str) -> AttemptResult {
        AttemptResult {
            attempt_id: id.into(),
            outcome: Outcome::Succeeded as i32,
            outputs_json: r#"{"hasChanges":true}"#.into(),
            error: String::new(),
            artifacts: vec![Artifact {
                name: "plan.tar.gz".into(),
                content: vec![0, 1, 2, 255],
                sha256: "abc".into(),
            }],
        }
    }

    #[test]
    fn put_get_list_remove() {
        let dir = tempfile::tempdir().unwrap();
        let outbox = Outbox::open(dir.path().join("outbox")).unwrap();
        assert!(outbox.list().unwrap().is_empty());

        outbox
            .put(&OutboxRecord::from_result(&result("a1"), 1))
            .unwrap();
        std::thread::sleep(std::time::Duration::from_millis(20));
        outbox
            .put(&OutboxRecord::from_result(&result("a2"), 2))
            .unwrap();
        assert_eq!(outbox.list().unwrap(), ["a1", "a2"]);
        assert!(outbox.contains("a1"));

        let back = outbox.get("a1").unwrap().unwrap().to_result().unwrap();
        assert_eq!(back, result("a1"));

        outbox.remove("a1").unwrap();
        outbox.remove("a1").unwrap();
        assert_eq!(outbox.list().unwrap(), ["a2"]);
        assert!(outbox.get("a1").unwrap().is_none());

        assert!(
            outbox
                .put(&OutboxRecord::from_result(&result("../x"), 1))
                .is_err()
        );
    }

    #[test]
    fn survives_reopen_and_quarantines_corrupt_files() {
        let dir = tempfile::tempdir().unwrap();
        let path = dir.path().join("outbox");
        {
            let outbox = Outbox::open(&path).unwrap();
            outbox
                .put(&OutboxRecord::from_result(&result("keep"), 1))
                .unwrap();
            fs::write(path.join("bad.json"), b"{not json").unwrap();
            fs::write(path.join(".keep.json.tmp-1-2"), b"partial").unwrap();
        }
        let outbox = Outbox::open(&path).unwrap();
        assert!(!path.join(".keep.json.tmp-1-2").exists());
        let mut ids = outbox.list().unwrap();
        ids.sort();
        assert_eq!(ids, ["bad", "keep"]);
        assert!(outbox.get("bad").unwrap().is_none());
        assert!(path.join("bad.json.corrupt").exists());
        assert_eq!(outbox.list().unwrap(), ["keep"]);
    }
}
