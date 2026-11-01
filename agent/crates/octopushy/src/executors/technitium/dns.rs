//! A raw DNS question to one member, so that the answer is that server's and no other's.

use std::net::SocketAddr;
use std::time::Duration;

use tokio::net::UdpSocket;

const TYPE_OPT: u16 = 41;
const OPTION_EDE: u16 = 15;
/// RFC 8914: "Blocked". Technitium sends it with every blocked answer, whatever the blocking
/// type and whether or not the name exists, so it proves more than NXDOMAIN does.
const EDE_BLOCKED: u16 = 15;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Answer {
    pub rcode: u8,
    pub answers: u16,
    pub blocked: bool,
}

impl Answer {
    pub fn resolves(&self) -> bool {
        self.rcode == 0 && self.answers > 0
    }
}

/// Asks `server` for the A records of `name`, with one retry.
pub async fn query(server: SocketAddr, name: &str, timeout: Duration) -> Result<Answer, String> {
    let id = fastrand::u16(..);
    let message = question(id, name)?;
    let socket = UdpSocket::bind(if server.is_ipv4() {
        "0.0.0.0:0"
    } else {
        "[::]:0"
    })
    .await
    .map_err(|e| format!("cannot open a socket: {e}"))?;
    socket
        .connect(server)
        .await
        .map_err(|e| format!("cannot reach {server}: {e}"))?;
    let mut buffer = [0u8; 4096];
    for _ in 0..2 {
        socket
            .send(&message)
            .await
            .map_err(|e| format!("cannot send to {server}: {e}"))?;
        // A stray or delayed datagram is not the answer: keep reading until the timeout.
        let waited = tokio::time::timeout(timeout, async {
            loop {
                let size = socket.recv(&mut buffer).await?;
                if size >= 2 && u16::from_be_bytes([buffer[0], buffer[1]]) == id {
                    return Ok::<usize, std::io::Error>(size);
                }
            }
        })
        .await;
        match waited {
            Ok(Ok(size)) => {
                return parse(&buffer[..size])
                    .ok_or_else(|| format!("{server} sent a malformed answer"));
            }
            Ok(Err(e)) => return Err(format!("{server} does not answer: {e}")),
            Err(_) => continue,
        }
    }
    Err(format!("{server} does not answer"))
}

fn question(id: u16, name: &str) -> Result<Vec<u8>, String> {
    let mut message = Vec::with_capacity(64);
    message.extend_from_slice(&id.to_be_bytes());
    message.extend_from_slice(&[0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 1]);
    for label in name.trim_end_matches('.').split('.') {
        if label.is_empty() || label.len() > 63 {
            return Err(format!("{name:?} is not a domain name"));
        }
        message.push(label.len() as u8);
        message.extend_from_slice(label.as_bytes());
    }
    message.push(0);
    message.extend_from_slice(&[0, 1, 0, 1]);
    // EDNS, without which there is nowhere for the extended error to go.
    message.extend_from_slice(&[0, 0, 41, 0x04, 0xd0, 0, 0, 0, 0, 0, 0]);
    Ok(message)
}

fn parse(m: &[u8]) -> Option<Answer> {
    let word = |at: usize| Some(u16::from_be_bytes([*m.get(at)?, *m.get(at + 1)?]));
    let rcode = m.get(3)? & 0x0f;
    let (qd, an, ns, ar) = (word(4)?, word(6)?, word(8)?, word(10)?);
    let mut at = 12;
    for _ in 0..qd {
        at = skip_name(m, at)? + 4;
    }
    let mut blocked = false;
    for record in 0..(u32::from(an) + u32::from(ns) + u32::from(ar)) {
        at = skip_name(m, at)?;
        let kind = word(at)?;
        let length = usize::from(word(at + 8)?);
        let data = at + 10;
        if kind == TYPE_OPT && record >= u32::from(an) + u32::from(ns) {
            let mut option = data;
            while option + 4 <= data + length {
                let (code, size) = (word(option)?, usize::from(word(option + 2)?));
                if code == OPTION_EDE && size >= 2 && word(option + 4)? == EDE_BLOCKED {
                    blocked = true;
                }
                option += 4 + size;
            }
        }
        at = data + length;
    }
    Some(Answer {
        rcode,
        answers: an,
        blocked,
    })
}

fn skip_name(m: &[u8], mut at: usize) -> Option<usize> {
    loop {
        let length = *m.get(at)?;
        if length & 0xc0 == 0xc0 {
            return Some(at + 2);
        }
        at += 1;
        if length == 0 {
            return Some(at);
        }
        at += usize::from(length);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn header(rcode: u8, an: u16, ns: u16, ar: u16) -> Vec<u8> {
        let mut m = vec![0x12, 0x34, 0x85, 0x80 | rcode, 0, 1];
        for count in [an, ns, ar] {
            m.extend_from_slice(&count.to_be_bytes());
        }
        m.extend_from_slice(&[3, b'b', b'a', b'd', 7]);
        m.extend_from_slice(b"example");
        m.extend_from_slice(&[3, b'c', b'o', b'm', 0, 0, 1, 0, 1]);
        m
    }

    fn opt(options: &[u8]) -> Vec<u8> {
        let mut r = vec![0, 0, 41, 4, 0xd0, 0, 0, 0, 0];
        r.extend_from_slice(&(options.len() as u16).to_be_bytes());
        r.extend_from_slice(options);
        r
    }

    #[test]
    fn a_blocked_answer_carries_the_extended_error() {
        // The bytes a real 15.6.2 sends: NXDOMAIN, an SOA, and EDE 15 with its report.
        let mut m = header(3, 0, 1, 1);
        m.extend_from_slice(&[0xc0, 0x0c, 0, 6, 0, 1, 0, 0, 0, 30, 0, 2, 0, 0]);
        let mut ede = vec![0, 15, 0, 9, 0, 15];
        ede.extend_from_slice(b"source");
        m.extend(opt(&ede));
        let answer = parse(&m).unwrap();
        assert_eq!(
            answer,
            Answer {
                rcode: 3,
                answers: 0,
                blocked: true
            }
        );
        assert!(!answer.resolves());
    }

    #[test]
    fn nxdomain_alone_is_not_blocked() {
        let mut m = header(3, 0, 0, 1);
        m.extend(opt(&[]));
        assert!(!parse(&m).unwrap().blocked);
    }

    #[test]
    fn a_stale_cache_notice_is_not_blocked() {
        // Technitium sends EDE 6 ("DNSSEC bogus" here) on some forwarded answers.
        let mut m = header(0, 1, 0, 1);
        m.extend_from_slice(&[0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 10]);
        m.extend(opt(&[0, 15, 0, 2, 0, 6]));
        let answer = parse(&m).unwrap();
        assert!(answer.resolves());
        assert!(!answer.blocked);
    }

    #[test]
    fn truncated_answers_are_refused() {
        let m = header(0, 1, 0, 0);
        assert_eq!(parse(&m), None);
        assert_eq!(parse(&[0, 1]), None);
    }

    #[test]
    fn names_must_be_names() {
        assert!(question(1, "bad..example").is_err());
        assert!(question(1, "example.com.").is_ok());
    }
}
