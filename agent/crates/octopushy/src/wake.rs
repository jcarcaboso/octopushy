//! Wake-on-LAN: one magic packet, sent from the agent process when the server asks. It holds no
//! credential and nothing comes back; a packet that reaches no host is not an error.

use std::net::{Ipv4Addr, SocketAddr};

use octopushy_proto::v1::WakeOnLan;
use tokio::net::UdpSocket;

/// Announced in `Hello` by agents that send Wake-on-LAN packets.
pub const CAPABILITY: &str = "wake-on-lan-v1";

/// The magic packet for `mac`: six 0xff bytes, then the MAC sixteen times.
pub fn magic_packet(mac: [u8; 6]) -> [u8; 102] {
    let mut packet = [0xff; 102];
    for chunk in packet[6..].as_chunks_mut::<6>().0 {
        *chunk = mac;
    }
    packet
}

/// Sends the packet the message asks for, logging the outcome.
pub fn spawn(message: WakeOnLan) {
    tokio::spawn(async move {
        match send(&message, 9).await {
            Ok(target) => tracing::info!(%target, "sent a Wake-on-LAN packet"),
            Err(e) => tracing::warn!(error = %e, "could not send a Wake-on-LAN packet"),
        }
    });
}

/// Port 9 is the discard port Wake-on-LAN uses; tests pass another.
async fn send(message: &WakeOnLan, port: u16) -> anyhow::Result<SocketAddr> {
    let mac: [u8; 6] = message
        .mac
        .as_slice()
        .try_into()
        .map_err(|_| anyhow::anyhow!("a MAC address is 6 bytes, not {}", message.mac.len()))?;
    let broadcast: Ipv4Addr = message
        .broadcast
        .parse()
        .map_err(|_| anyhow::anyhow!("{:?} is not an IPv4 address", message.broadcast))?;
    let target = SocketAddr::from((broadcast, port));
    let socket = UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0)).await?;
    socket.set_broadcast(true)?;
    socket.send_to(&magic_packet(mac), target).await?;
    Ok(target)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn packet_is_sync_stream_then_mac_sixteen_times() {
        let mac = [0xa8, 0xa1, 0x59, 0x3c, 0x0e, 0x72];
        let packet = magic_packet(mac);
        assert_eq!(packet[..6], [0xff; 6]);
        assert!(packet[6..].chunks(6).all(|c| c == mac));
    }

    #[tokio::test]
    async fn sends_the_packet_to_the_address() {
        let listener = UdpSocket::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let mac = [1, 2, 3, 4, 5, 6];
        send(
            &WakeOnLan {
                mac: mac.to_vec(),
                broadcast: "127.0.0.1".into(),
            },
            port,
        )
        .await
        .unwrap();
        let mut buf = [0; 128];
        let n = listener.recv(&mut buf).await.unwrap();
        assert_eq!(buf[..n], magic_packet(mac));
    }

    #[tokio::test]
    async fn refuses_a_bad_mac_or_address() {
        let short = WakeOnLan {
            mac: vec![1, 2, 3],
            broadcast: "255.255.255.255".into(),
        };
        assert!(send(&short, 9).await.is_err());
        let v6 = WakeOnLan {
            mac: vec![0; 6],
            broadcast: "::1".into(),
        };
        assert!(send(&v6, 9).await.is_err());
    }
}
