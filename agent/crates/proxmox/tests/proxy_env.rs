//! What the client does with proxy variables in its environment, against what [`proxied`] says.
//!
//! Its own test binary with one test: it changes the process environment, which no other test
//! may share.

use std::time::Duration;

use octopushy_proxmox::{ApiToken, Client, ClientConfig, Tls, proxied};
use tokio::io::AsyncReadExt;
use tokio::net::TcpListener;

fn client(endpoint: &str) -> Client {
    Client::new(ClientConfig {
        endpoint: endpoint.into(),
        token: ApiToken::new("octopushy@pve!agent=00000000-0000-0000-0000-000000000000").unwrap(),
        tls: Tls::Insecure,
        timeout: Duration::from_secs(3),
    })
    .unwrap()
}

/// The first bytes a request sends to `proxy`, if it goes there at all.
async fn first_bytes(proxy: &TcpListener, endpoint: &str) -> Option<String> {
    let c = client(endpoint);
    let request = tokio::spawn(async move {
        let _ = c.version().await;
    });
    let seen = tokio::time::timeout(Duration::from_secs(3), proxy.accept())
        .await
        .ok()
        .map(|accepted| accepted.unwrap().0);
    let text = match seen {
        Some(mut stream) => {
            let mut buf = vec![0u8; 256];
            let n = stream.read(&mut buf).await.unwrap_or(0);
            Some(String::from_utf8_lossy(&buf[..n]).into_owned())
        }
        None => None,
    };
    request.abort();
    text
}

#[tokio::test]
async fn proxied_matches_what_the_client_does() {
    let proxy = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://127.0.0.1:{}", proxy.local_addr().unwrap().port());
    let endpoint = "https://pve.example.invalid:8006/";
    for name in [
        "HTTPS_PROXY",
        "https_proxy",
        "HTTP_PROXY",
        "http_proxy",
        "ALL_PROXY",
        "all_proxy",
        "NO_PROXY",
        "no_proxy",
        "REQUEST_METHOD",
    ] {
        // SAFETY: this binary has one test, so nothing reads the environment concurrently.
        unsafe { std::env::remove_var(name) };
    }

    assert!(!proxied(endpoint).unwrap(), "no variables: direct");

    // SAFETY: as above.
    unsafe { std::env::set_var("HTTPS_PROXY", &url) };
    assert!(proxied(endpoint).unwrap());
    let sent = first_bytes(&proxy, endpoint)
        .await
        .expect("the client used the proxy");
    assert!(
        sent.starts_with("CONNECT pve.example.invalid:8006"),
        "{sent:?}"
    );

    // NO_PROXY exempts the host: both say direct.
    // SAFETY: as above.
    unsafe { std::env::set_var("NO_PROXY", "example.invalid") };
    assert!(!proxied(endpoint).unwrap());
    assert!(first_bytes(&proxy, endpoint).await.is_none());

    // ALL_PROXY alone counts for https too.
    // SAFETY: as above.
    unsafe {
        std::env::remove_var("HTTPS_PROXY");
        std::env::remove_var("NO_PROXY");
        std::env::set_var("ALL_PROXY", &url);
    }
    assert!(proxied(endpoint).unwrap());
    assert!(first_bytes(&proxy, endpoint).await.is_some());
}
