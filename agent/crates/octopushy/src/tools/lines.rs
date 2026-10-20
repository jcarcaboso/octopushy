//! Splitting tool output into lines without unbounded buffering.

use tokio::io::{AsyncBufReadExt, AsyncRead, BufReader};

use crate::attempts::logs::MAX_LINE_BYTES;

/// Calls `f` for each line of `reader` (lossy UTF-8, without the newline).
/// Lines longer than [`MAX_LINE_BYTES`] are cut; the rest of such a line is
/// skipped.
pub async fn for_each_line<R: AsyncRead + Unpin>(
    reader: R,
    mut f: impl FnMut(String),
) -> std::io::Result<()> {
    let mut reader = BufReader::with_capacity(64 * 1024, reader);
    let mut line: Vec<u8> = Vec::new();
    let mut truncated = false;
    loop {
        let buf = reader.fill_buf().await?;
        if buf.is_empty() {
            if !line.is_empty() || truncated {
                f(finish(&line, truncated));
            }
            return Ok(());
        }
        let (chunk, consumed, end) = match buf.iter().position(|&b| b == b'\n') {
            Some(i) => (&buf[..i], i + 1, true),
            None => (buf, buf.len(), false),
        };
        let room = MAX_LINE_BYTES.saturating_sub(line.len());
        if chunk.len() > room {
            line.extend_from_slice(&chunk[..room]);
            truncated = true;
        } else {
            line.extend_from_slice(chunk);
        }
        reader.consume(consumed);
        if end {
            f(finish(&line, truncated));
            line.clear();
            truncated = false;
        }
    }
}

fn finish(line: &[u8], truncated: bool) -> String {
    let mut s = String::from_utf8_lossy(line).into_owned();
    if s.ends_with('\r') {
        s.pop();
    }
    if truncated {
        s.push_str(" [line truncated]");
    }
    s
}

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn splits_and_caps() {
        let long = "y".repeat(MAX_LINE_BYTES + 10);
        let input = format!("one\r\ntwo\n{long}\nlast");
        let mut lines = Vec::new();
        for_each_line(input.as_bytes(), |l| lines.push(l))
            .await
            .unwrap();
        assert_eq!(lines.len(), 4);
        assert_eq!(lines[0], "one");
        assert_eq!(lines[1], "two");
        assert!(lines[2].ends_with("[line truncated]"));
        assert_eq!(lines[3], "last");
    }
}
