//! Per-attempt log buffers.
//!
//! Lines are numbered from 0 per attempt and kept in a bounded buffer until the
//! attempt's result is acknowledged, so a new connection can resend them; the
//! server drops the ones it already has by sequence number.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};
use std::time::{SystemTime, UNIX_EPOCH};

use octopushy_proto::v1::{LogLine, LogStream};
use tokio::sync::Notify;

use crate::masking::Masker;

/// Longest line kept; the rest is cut off.
pub const MAX_LINE_BYTES: usize = 16 * 1024;
/// Lines per `AttemptLog` message, and the backlog that triggers an early flush.
pub const MAX_BATCH_LINES: usize = 100;
const MAX_RETAINED_LINES: usize = 20_000;
const MAX_RETAINED_BYTES: usize = 8 * 1024 * 1024;

#[derive(Default)]
struct Inner {
    lines: VecDeque<LogLine>,
    /// Sequence number of `lines[0]`.
    first_seq: u64,
    bytes: usize,
    since_wake: usize,
}

/// The retained lines of one attempt.
#[derive(Default)]
pub struct LogBuffer {
    inner: Mutex<Inner>,
}

impl LogBuffer {
    /// Appends a line; returns whether enough lines piled up to flush early.
    fn push(&self, line: LogLine) -> bool {
        let mut inner = self.inner.lock().unwrap();
        inner.bytes += line.text.len();
        inner.lines.push_back(line);
        // Drop the oldest lines beyond the caps. They were most likely sent
        // already; if not, the server sees a gap in the sequence.
        while inner.lines.len() > MAX_RETAINED_LINES || inner.bytes > MAX_RETAINED_BYTES {
            match inner.lines.pop_front() {
                Some(old) => {
                    inner.bytes -= old.text.len();
                    inner.first_seq += 1;
                }
                None => break,
            }
        }
        inner.since_wake += 1;
        if inner.since_wake >= MAX_BATCH_LINES {
            inner.since_wake = 0;
            true
        } else {
            false
        }
    }

    /// The sequence number the next line will get.
    pub fn next_seq(&self) -> u64 {
        let inner = self.inner.lock().unwrap();
        inner.first_seq + inner.lines.len() as u64
    }

    /// Up to `max` lines starting at `seq` (or at the oldest retained line, if
    /// `seq` was dropped), with the sequence number of the first.
    pub fn batch_from(&self, seq: u64, max: usize) -> Option<(u64, Vec<LogLine>)> {
        let inner = self.inner.lock().unwrap();
        let start = seq.max(inner.first_seq);
        let offset = (start - inner.first_seq) as usize;
        if offset >= inner.lines.len() {
            return None;
        }
        let lines: Vec<LogLine> = inner.lines.iter().skip(offset).take(max).cloned().collect();
        Some((start, lines))
    }
}

/// Writes masked lines into an attempt's buffer.
#[derive(Clone)]
pub struct AttemptLogger {
    buffer: Arc<LogBuffer>,
    masker: Arc<Masker>,
    wake: Arc<Notify>,
}

impl AttemptLogger {
    pub fn new(buffer: Arc<LogBuffer>, masker: Arc<Masker>, wake: Arc<Notify>) -> Self {
        Self {
            buffer,
            masker,
            wake,
        }
    }

    /// Masks `text` with this attempt's secrets.
    pub fn mask(&self, text: &str) -> String {
        self.masker.mask(text).into_owned()
    }

    pub fn masker(&self) -> &Arc<Masker> {
        &self.masker
    }

    /// Logs one line per line of `text`; returns the last stored (masked) line.
    pub fn line(&self, stream: LogStream, text: &str) -> String {
        let mut last = String::new();
        for raw in text.split('\n') {
            let clean = sanitize(raw);
            let masked = self.masker.mask(&clean).into_owned();
            let line = LogLine {
                unix_ms: unix_ms(),
                stream: stream as i32,
                text: masked.clone(),
            };
            if self.buffer.push(line) {
                self.wake.notify_one();
            }
            last = masked;
        }
        last
    }

    /// A line the agent writes itself, such as which command it starts.
    pub fn agent(&self, text: impl AsRef<str>) {
        let text = self.line(LogStream::Agent, text.as_ref());
        tracing::debug!(target: "octopushy::attempt", "{text}");
    }

    pub fn stdout(&self, text: &str) -> String {
        self.line(LogStream::Stdout, text)
    }

    pub fn stderr(&self, text: &str) -> String {
        self.line(LogStream::Stderr, text)
    }
}

fn unix_ms() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0)
}

/// Removes terminal escape sequences and control characters, and caps the
/// length.
pub fn sanitize(raw: &str) -> String {
    let mut out = String::with_capacity(raw.len().min(MAX_LINE_BYTES));
    let mut chars = raw.chars().peekable();
    while let Some(c) = chars.next() {
        if c == '\u{1b}' {
            // CSI: ESC [ params final-byte; other escapes: ESC + one char.
            if chars.peek() == Some(&'[') {
                chars.next();
                for c in chars.by_ref() {
                    if ('\u{40}'..='\u{7e}').contains(&c) {
                        break;
                    }
                }
            } else {
                chars.next();
            }
            continue;
        }
        if c == '\t' {
            out.push(' ');
        } else if !c.is_control() {
            out.push(c);
        }
        if out.len() >= MAX_LINE_BYTES {
            out.push_str(" [line truncated]");
            break;
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn logger(secrets: &[&str]) -> (AttemptLogger, Arc<LogBuffer>) {
        let buffer = Arc::new(LogBuffer::default());
        let masker = Arc::new(Masker::new(secrets.iter().copied()));
        (
            AttemptLogger::new(buffer.clone(), masker, Arc::new(Notify::new())),
            buffer,
        )
    }

    #[test]
    fn numbers_masks_and_batches() {
        let (log, buf) = logger(&["hunter2"]);
        log.agent("$ tofu init");
        log.stdout("password is hunter2\nsecond line");
        assert_eq!(buf.next_seq(), 3);
        let (first, lines) = buf.batch_from(0, 100).unwrap();
        assert_eq!(first, 0);
        assert_eq!(lines.len(), 3);
        assert_eq!(lines[0].stream, LogStream::Agent as i32);
        assert_eq!(lines[1].text, "password is ***");
        assert_eq!(lines[2].text, "second line");
        let (first, lines) = buf.batch_from(2, 100).unwrap();
        assert_eq!((first, lines.len()), (2, 1));
        assert!(buf.batch_from(3, 100).is_none());
    }

    #[test]
    fn retains_a_bounded_tail() {
        let (log, buf) = logger(&[]);
        for i in 0..(MAX_RETAINED_LINES + 10) {
            log.stdout(&format!("line {i}"));
        }
        let (first, lines) = buf.batch_from(0, 5).unwrap();
        assert_eq!(first, 10);
        assert_eq!(lines[0].text, "line 10");
        assert_eq!(buf.next_seq(), (MAX_RETAINED_LINES + 10) as u64);
    }

    #[test]
    fn sanitizes() {
        assert_eq!(sanitize("\u{1b}[1m\u{1b}[32mGreen\u{1b}[0m\r"), "Green");
        assert_eq!(sanitize("a\tb\u{7}c"), "a bc");
        let long = "x".repeat(MAX_LINE_BYTES * 2);
        assert!(sanitize(&long).ends_with("[line truncated]"));
    }
}
