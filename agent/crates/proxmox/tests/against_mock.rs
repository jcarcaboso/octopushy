//! The client against the `mock-proxmox` server, over HTTPS with its CA as the
//! only trust root.

use std::time::Duration;

use mock_proxmox::{Fixture, Options};
use octopushy_proxmox::{ApiToken, Client, ClientConfig, Error, GuestKind, Tls, ipv4_addresses};

async fn start_mock() -> mock_proxmox::RunningMock {
    mock_proxmox::start(
        Fixture::example(),
        Options {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap()
}

fn client(mock: &mock_proxmox::RunningMock, token: &str, tls: Tls) -> Client {
    Client::new(ClientConfig {
        endpoint: mock.endpoint(),
        token: ApiToken::new(token).unwrap(),
        tls,
        timeout: Duration::from_secs(10),
    })
    .unwrap()
}

#[tokio::test]
async fn reads_the_installation_with_a_custom_ca() {
    let mock = start_mock().await;
    let c = client(
        &mock,
        &mock.token(),
        Tls::CustomCa(mock.ca_pem().unwrap().to_owned()),
    );

    assert_eq!(c.version().await.unwrap().version, "8.4.1");

    let status = c.cluster_status().await.unwrap();
    let cluster = status.cluster().unwrap();
    assert_eq!(cluster.name, "home");
    assert!(cluster.quorate);
    assert_eq!(status.nodes().count(), 2);

    let nodes = c.nodes().await.unwrap();
    assert_eq!(
        nodes.iter().map(|n| n.node.as_str()).collect::<Vec<_>>(),
        ["korriban", "moraband"]
    );
    assert!(nodes[0].is_online());
    assert_eq!(nodes[0].maxcpu, Some(16));
    assert_eq!(nodes[0].maxmem, Some(68_719_476_736));

    let vms = c.vm_resources().await.unwrap();
    assert_eq!(vms.len(), 5);
    let crt = vms.iter().find(|v| v.vmid == 210).unwrap();
    assert_eq!(crt.name.as_deref(), Some("krb-crt-01"));
    assert_eq!(crt.kind, GuestKind::Qemu);
    assert_eq!(crt.tags, ["nixos", "opentofu"]);
    assert!(!crt.template);
    let template = vms.iter().find(|v| v.vmid == 9000).unwrap();
    assert!(template.template, "template is sent as 1");
    let unmanaged = vms.iter().find(|v| v.vmid == 150).unwrap();
    assert!(unmanaged.tags.is_empty());

    assert!(c.find_vm(999).await.unwrap().is_none());
    assert_eq!(c.find_vm(310).await.unwrap().unwrap().node, "moraband");
}

#[tokio::test]
async fn vm_status_config_protection_and_guest_agent() {
    let mock = start_mock().await;
    let c = client(
        &mock,
        &mock.token(),
        Tls::CustomCa(mock.ca_pem().unwrap().to_owned()),
    );

    let status = c.vm_status("korriban", 210).await.unwrap();
    assert_eq!(status.status, "running");

    let config = c.vm_config("korriban", 210).await.unwrap();
    assert!(config.protection);
    assert_eq!(config.name.as_deref(), Some("krb-crt-01"));

    c.set_vm_protection("korriban", 210, false).await.unwrap();
    assert!(!c.vm_config("korriban", 210).await.unwrap().protection);
    assert!(!mock.vm(210).unwrap().protection);
    c.set_vm_protection("korriban", 210, true).await.unwrap();
    assert!(mock.vm(210).unwrap().protection);

    // A VM on another node is "not found", as Proxmox reports a missing config.
    match c.vm_config("moraband", 210).await {
        Err(Error::NotFound(m)) => assert!(m.contains("does not exist"), "{m}"),
        other => panic!("unexpected {other:?}"),
    }
    match c.set_vm_protection("korriban", 4242, false).await {
        Err(Error::NotFound(_)) => {}
        other => panic!("unexpected {other:?}"),
    }

    let ifaces = c.guest_network_interfaces("korriban", 210).await.unwrap();
    let ips = ipv4_addresses(&ifaces);
    assert_eq!(ips, ["10.1.0.21".parse::<std::net::Ipv4Addr>().unwrap()]);

    match c.guest_network_interfaces("korriban", 211).await {
        Err(Error::GuestAgentUnavailable(m)) => assert!(m.contains("guest agent"), "{m}"),
        other => panic!("unexpected {other:?}"),
    }
    // A stopped VM has no guest agent either.
    match c.guest_network_interfaces("moraband", 150).await {
        Err(Error::GuestAgentUnavailable(m)) => assert!(m.contains("not running"), "{m}"),
        other => panic!("unexpected {other:?}"),
    }

    let perms = c.permissions(Some("/vms/210")).await.unwrap();
    assert!(perms.has("/vms/210", "VM.Audit"));
    assert!(!perms.has("/vms/211", "VM.Audit"));
}

#[tokio::test]
async fn wrong_token_is_unauthorized() {
    let mock = start_mock().await;
    let c = client(
        &mock,
        "octopushy@pve!agent=wrong-secret",
        Tls::CustomCa(mock.ca_pem().unwrap().to_owned()),
    );
    let err = c.version().await.unwrap_err();
    assert!(matches!(err, Error::Unauthorized), "{err:?}");
    assert!(!err.to_string().contains("wrong-secret"));
}

#[tokio::test]
async fn tls_verification_modes() {
    let mock = start_mock().await;
    let token = mock.token();

    // The system trust store does not know the mock's CA.
    let err = client(&mock, &token, Tls::Verify)
        .version()
        .await
        .unwrap_err();
    assert!(matches!(err, Error::Transport(_)), "{err:?}");

    // Another CA is rejected too.
    let other = mock_proxmox::start(
        Fixture::example(),
        Options {
            listen: "127.0.0.1:0".parse().unwrap(),
            tls: true,
        },
    )
    .await
    .unwrap();
    let err = client(
        &mock,
        &token,
        Tls::CustomCa(other.ca_pem().unwrap().to_owned()),
    )
    .version()
    .await
    .unwrap_err();
    assert!(matches!(err, Error::Transport(_)), "{err:?}");
    assert!(!err.to_string().contains(&token));

    // Insecure accepts it.
    assert!(client(&mock, &token, Tls::Insecure).version().await.is_ok());

    // A malformed CA is a configuration error.
    let err = Client::new(ClientConfig {
        endpoint: mock.endpoint(),
        token: ApiToken::new(token).unwrap(),
        tls: Tls::CustomCa("not a certificate".into()),
        timeout: Duration::from_secs(5),
    })
    .unwrap_err();
    assert!(matches!(err, Error::Config(_)), "{err:?}");
}

#[tokio::test]
async fn unreachable_endpoint_is_a_transport_error() {
    // Bind and drop a listener to get a port nothing listens on.
    let port = std::net::TcpListener::bind("127.0.0.1:0")
        .unwrap()
        .local_addr()
        .unwrap()
        .port();
    let c = Client::new(ClientConfig {
        endpoint: format!("https://127.0.0.1:{port}/"),
        token: ApiToken::new("a@pve!t=s3cr3t").unwrap(),
        tls: Tls::Insecure,
        timeout: Duration::from_secs(5),
    })
    .unwrap();
    let err = c.version().await.unwrap_err();
    assert!(matches!(err, Error::Transport(_)), "{err:?}");
    assert!(err.is_transient());
    assert!(!err.to_string().contains("s3cr3t"));
}
