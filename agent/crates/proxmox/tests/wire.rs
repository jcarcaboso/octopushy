//! Exact wire behavior against a scripted HTTP server: the auth header, the
//! request shapes, lenient decoding, and error mapping.

use std::time::Duration;

use octopushy_proxmox::{ApiToken, Client, ClientConfig, Error, Tls};
use serde_json::json;
use wiremock::matchers::{body_json, body_string, header, method, path, query_param};
use wiremock::{Mock, MockServer, ResponseTemplate};

const TOKEN: &str = "octopushy@pve!agent=11111111-2222-3333-4444-555555555555";

#[tokio::test]
async fn guest_exec_sends_an_argument_array_and_decodes_completed_status() {
    let server = MockServer::start().await;
    Mock::given(method("POST"))
        .and(path("/api2/json/nodes/node/qemu/310/agent/exec"))
        .and(wiremock::matchers::body_json(
            json!({"command":["/bin/echo","a b"]}),
        ))
        .respond_with(data(json!({"pid":123})))
        .expect(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/node/qemu/310/agent/exec-status"))
        .and(query_param("pid", "123"))
        .respond_with(data(json!({"exited":1,"exitcode":0,"out-data":"ok"})))
        .expect(1)
        .mount(&server)
        .await;
    let c = client(&server);
    let pid = c
        .guest_exec("node", 310, &["/bin/echo".into(), "a b".into()], None)
        .await
        .unwrap();
    assert_eq!(pid, 123);
    let status = c.guest_exec_status("node", 310, pid).await.unwrap();
    assert!(status.exited);
    assert_eq!(status.exitcode, Some(0));
    assert_eq!(status.stdout, "ok");
}

fn client(server: &MockServer) -> Client {
    Client::new(ClientConfig {
        endpoint: format!("{}/", server.uri()),
        token: ApiToken::new(TOKEN).unwrap(),
        tls: Tls::Verify,
        timeout: Duration::from_secs(5),
    })
    .unwrap()
}

fn data(v: serde_json::Value) -> ResponseTemplate {
    ResponseTemplate::new(200).set_body_json(json!({ "data": v }))
}

#[tokio::test]
async fn guest_exec_sends_plain_stdin_and_polls_pid_without_returning_output() {
    let server = MockServer::start().await;
    let command = ["/usr/bin/timeout", "240s", "/bin/sh"].map(String::from);
    let script = "set -eu\nprintf '%s' 'private-input'\n";
    Mock::given(method("POST"))
        .and(path("/api2/json/nodes/n1/qemu/310/agent/exec"))
        .and(header(
            "authorization",
            format!("PVEAPIToken={TOKEN}").as_str(),
        ))
        .and(body_json(
            json!({ "command": command, "input-data": script }),
        ))
        .respond_with(data(json!({ "pid": 42 })))
        .expect(1)
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/n1/qemu/310/agent/exec-status"))
        .and(query_param("pid", "42"))
        .respond_with(data(json!({
            "exited": 1, "exitcode": 0, "out-data": "secret", "err-data": "secret"
        })))
        .expect(1)
        .mount(&server)
        .await;
    let c = client(&server);
    let pid = c
        .guest_exec("n1", 310, &command, Some(script))
        .await
        .unwrap();
    assert_eq!(pid, 42);
    let status = c.guest_exec_status("n1", 310, pid).await.unwrap();
    assert!(status.exited);
    assert_eq!(status.exitcode, Some(0));
    assert!(!format!("{status:?}").contains("secret"));
    assert!(matches!(
        c.guest_exec("n1", 310, &command, Some(&"x".repeat(65537)))
            .await,
        Err(Error::Config(_))
    ));
}

#[tokio::test]
async fn sends_the_token_header() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/version"))
        .and(header(
            "authorization",
            format!("PVEAPIToken={TOKEN}").as_str(),
        ))
        .respond_with(data(json!({ "version": "8.4.1", "release": "8.4" })))
        .expect(1)
        .mount(&server)
        .await;
    let v = client(&server).version().await.unwrap();
    assert_eq!(v.version, "8.4.1");
    assert_eq!(v.release.as_deref(), Some("8.4"));
}

#[tokio::test]
async fn decodes_numbers_and_booleans_leniently() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/cluster/resources"))
        .and(query_param("type", "vm"))
        .respond_with(data(json!([
            { "vmid": "300", "name": "b", "node": "n1", "status": "running", "type": "qemu",
              "template": "0", "maxcpu": "4", "maxmem": 1073741824.0, "uptime": "12", "tags": "x;y" },
            { "vmid": 100, "name": 1234, "node": "n1", "status": "stopped", "type": "lxc",
              "template": 1, "maxcpu": 0.5, "tags": "" },
            { "vmid": 200, "node": "n2", "type": "qemu", "template": true }
        ])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes"))
        .respond_with(data(json!([
            { "node": "n2", "status": "offline" },
            { "node": "n1", "status": "online", "cpu": "0.25", "maxcpu": 8, "mem": "1024",
              "maxmem": 4096, "uptime": 99 }
        ])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/cluster/status"))
        .respond_with(data(json!([
            { "type": "node", "name": "n1", "online": 1, "local": 1, "nodeid": 1 }
        ])))
        .mount(&server)
        .await;

    let c = client(&server);
    let vms = c.vm_resources().await.unwrap();
    assert_eq!(
        vms.iter().map(|v| v.vmid).collect::<Vec<_>>(),
        [100, 200, 300]
    );
    assert_eq!(vms[0].name.as_deref(), Some("1234"));
    assert!(vms[0].template);
    assert!(vms[0].tags.is_empty());
    assert_eq!(vms[0].maxcpu, Some(0.5));
    assert!(vms[1].template);
    assert_eq!(vms[1].status, "");
    assert_eq!(vms[2].tags, ["x", "y"]);
    assert_eq!(vms[2].maxcpu, Some(4.0));
    assert_eq!(vms[2].maxmem, Some(1_073_741_824));
    assert_eq!(vms[2].uptime, Some(12));

    let nodes = c.nodes().await.unwrap();
    assert_eq!(nodes[0].node, "n1");
    assert_eq!(nodes[0].cpu, Some(0.25));
    assert_eq!(nodes[0].mem, Some(1024));
    assert!(!nodes[1].is_online());
    assert_eq!(nodes[1].cpu, None);

    // A standalone host has no cluster entry.
    let status = c.cluster_status().await.unwrap();
    assert!(status.cluster().is_none());
    assert_eq!(status.nodes().count(), 1);
}

#[tokio::test]
async fn puts_protection_as_a_form() {
    let server = MockServer::start().await;
    Mock::given(method("PUT"))
        .and(path("/api2/json/nodes/korriban/qemu/210/config"))
        .and(header("content-type", "application/x-www-form-urlencoded"))
        .and(body_string("protection=0"))
        .respond_with(data(serde_json::Value::Null))
        .expect(1)
        .mount(&server)
        .await;
    client(&server)
        .set_vm_protection("korriban", 210, false)
        .await
        .unwrap();
}

#[tokio::test]
async fn maps_errors() {
    let server = MockServer::start().await;
    Mock::given(path("/api2/json/version"))
        .respond_with(ResponseTemplate::new(401))
        .mount(&server)
        .await;
    Mock::given(path("/api2/json/nodes"))
        .respond_with(ResponseTemplate::new(403).set_body_json(
            json!({ "data": null, "message": "Permission check failed (/, Sys.Audit)\n" }),
        ))
        .mount(&server)
        .await;
    Mock::given(path("/api2/json/cluster/status"))
        .respond_with(ResponseTemplate::new(502).set_body_string("<html>bad gateway</html>"))
        .mount(&server)
        .await;
    Mock::given(path("/api2/json/cluster/resources"))
        .respond_with(ResponseTemplate::new(200).set_body_string("{\"data\": 42}"))
        .mount(&server)
        .await;
    Mock::given(path(
        "/api2/json/nodes/n1/qemu/5/agent/network-get-interfaces",
    ))
    .respond_with(
        ResponseTemplate::new(500)
            .set_body_json(json!({ "data": null, "message": "QEMU guest agent is not running\n" })),
    )
    .mount(&server)
    .await;
    Mock::given(path("/api2/json/nodes/n1/qemu/6/agent/network-get-interfaces"))
        .respond_with(ResponseTemplate::new(500).set_body_json(
            json!({ "data": null, "message": "Configuration file 'nodes/n1/qemu-server/6.conf' does not exist\n" }),
        ))
        .mount(&server)
        .await;
    Mock::given(path("/api2/json/access/permissions"))
        .respond_with(
            ResponseTemplate::new(400)
                .set_body_json(json!({ "data": null, "errors": { "path": "invalid format" } })),
        )
        .mount(&server)
        .await;

    let c = client(&server);
    assert!(matches!(c.version().await, Err(Error::Unauthorized)));
    match c.nodes().await {
        Err(Error::Forbidden(m)) => assert!(m.contains("Sys.Audit"), "{m}"),
        other => panic!("{other:?}"),
    }
    match c.cluster_status().await {
        Err(Error::Status {
            status: 502,
            message,
        }) => assert!(message.contains("bad gateway")),
        other => panic!("{other:?}"),
    }
    match c.vm_resources().await {
        Err(Error::Decode { path, .. }) => assert_eq!(path, "/cluster/resources"),
        other => panic!("{other:?}"),
    }
    assert!(matches!(
        c.guest_network_interfaces("n1", 5).await,
        Err(Error::GuestAgentUnavailable(_))
    ));
    assert!(matches!(
        c.guest_network_interfaces("n1", 6).await,
        Err(Error::NotFound(_))
    ));
    match c.permissions(Some("bad")).await {
        Err(Error::Status {
            status: 400,
            message,
        }) => assert!(message.contains("invalid format")),
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn reads_a_containers_status_config_and_interfaces_through_lxc() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/lxc/320/status/current"))
        .respond_with(data(
            json!({ "vmid": 320, "status": "running", "name": "ct", "uptime": 9, "type": "lxc" }),
        ))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/lxc/320/config"))
        .respond_with(data(json!({
            "hostname": "ct", "protection": 1, "unprivileged": 1, "features": "nesting=1",
            "tags": "octopushy;home", "description": "Managed by Octopushy as home/korriban/ct.\n",
            "digest": "abc"
        })))
        .mount(&server)
        .await;
    // Proxmox 8.2 and later list every address in the guest agent's form too.
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/lxc/320/interfaces"))
        .respond_with(data(json!([
            { "name": "lo", "hwaddr": "00:00:00:00:00:00", "inet": "127.0.0.1/8", "inet6": "::1/128" },
            { "name": "eth0", "hwaddr": "bc:24:11:00:00:02", "hardware-address": "bc:24:11:00:00:02",
              "inet": "10.1.0.41/24", "inet6": "fe80::1/64",
              "ip-addresses": [
                { "ip-address": "10.1.0.40", "ip-address-type": "inet", "prefix": 24 },
                { "ip-address": "10.1.0.41", "ip-address-type": "inet", "prefix": 24 },
                { "ip-address": "fe80::1", "ip-address-type": "inet6", "prefix": 64 }
              ] }
        ])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/lxc/321/interfaces"))
        .respond_with(data(serde_json::Value::Null))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/lxc/404/config"))
        .respond_with(ResponseTemplate::new(500).set_body_json(
            json!({ "data": null, "message": "Configuration file 'nodes/korriban/lxc/404.conf' does not exist\n" }),
        ))
        .mount(&server)
        .await;
    Mock::given(method("PUT"))
        .and(path("/api2/json/nodes/korriban/lxc/320/config"))
        .and(header("content-type", "application/x-www-form-urlencoded"))
        .and(body_string("protection=1"))
        .respond_with(data(serde_json::Value::Null))
        .expect(1)
        .mount(&server)
        .await;

    let c = client(&server);
    assert_eq!(
        c.ct_status("korriban", 320).await.unwrap().status,
        "running"
    );
    let config = c.ct_config("korriban", 320).await.unwrap();
    assert_eq!(
        (
            config.hostname.as_deref(),
            config.protection,
            config.unprivileged,
            config.tags.len()
        ),
        (Some("ct"), true, true, 2)
    );
    assert!(
        config
            .description
            .unwrap()
            .starts_with("Managed by Octopushy as home/korriban/ct.")
    );
    let interfaces = c.ct_interfaces("korriban", 320).await.unwrap().unwrap();
    let addresses: Vec<String> = octopushy_proxmox::ct_ipv4_addresses(&interfaces)
        .iter()
        .map(ToString::to_string)
        .collect();
    // inet first, then any further IPv4 in the list; never loopback, never twice.
    assert_eq!(addresses, ["10.1.0.41", "10.1.0.40"]);
    assert!(c.ct_interfaces("korriban", 321).await.unwrap().is_none());
    assert!(matches!(
        c.ct_config("korriban", 404).await,
        Err(Error::NotFound(_))
    ));
    c.set_ct_protection("korriban", 320, true).await.unwrap();
}

#[tokio::test]
async fn lists_a_guests_tasks_and_reads_a_log() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/tasks"))
        .and(query_param("vmid", "320"))
        .and(query_param("source", "all"))
        .respond_with(data(json!([
            { "upid": "UPID:korriban:1:2:3:vzcreate:320:root@pam:", "type": "vzcreate", "id": "320",
              "user": "root@pam", "starttime": 1730191360, "endtime": 1730191372, "status": "OK" }
        ])))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path(
            "/api2/json/nodes/korriban/tasks/UPID:korriban:1:2:3:vzcreate:320:root@pam:/log",
        ))
        .respond_with(data(
            json!([{ "n": 2, "t": "TASK OK" }, { "n": 1, "t": "extracting archive" }]),
        ))
        .mount(&server)
        .await;
    let c = client(&server);
    let tasks = c.guest_tasks("korriban", 320).await.unwrap();
    assert_eq!(
        (tasks[0].kind.as_str(), tasks[0].status.as_deref()),
        ("vzcreate", Some("OK"))
    );
    let log = c.task_log("korriban", &tasks[0].upid).await.unwrap();
    assert_eq!(
        log.iter().map(|l| l.t.as_str()).collect::<Vec<_>>(),
        ["extracting archive", "TASK OK"]
    );
}

#[tokio::test]
async fn reads_a_nodes_clock() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/time"))
        .respond_with(data(
            json!({ "time": 1800000000, "localtime": 1800003600, "timezone": "Europe/Madrid" }),
        ))
        .mount(&server)
        .await;
    assert_eq!(
        client(&server).node_time("korriban").await.unwrap(),
        1_800_000_000
    );
}

#[tokio::test]
async fn review_guest_task_history_is_not_silently_limited_to_one_page() {
    let server = MockServer::start().await;
    let page: Vec<_> = (0..1000)
        .map(|i| json!({"upid": format!("task-{i}"), "type": "vzstart", "id": "320"}))
        .collect();
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/tasks"))
        .respond_with(data(json!(page)))
        .mount(&server)
        .await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/tasks"))
        .and(query_param("start", "1000"))
        .respond_with(data(
            json!([{"upid":"older-create", "type":"vzcreate", "id":"320"}]),
        ))
        .with_priority(1)
        .mount(&server)
        .await;
    let tasks = client(&server).guest_tasks("korriban", 320).await.unwrap();
    assert_eq!(tasks.len(), 1001, "the history endpoint has another page");
}

#[tokio::test]
async fn a_server_that_ignores_start_is_not_taken_for_a_whole_history() {
    let server = MockServer::start().await;
    let page: Vec<_> = (0..500)
        .map(|i| json!({"upid": format!("task-{i}"), "type": "vzstart", "id": "320"}))
        .collect();
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/tasks"))
        .respond_with(data(json!(page)))
        .mount(&server)
        .await;
    let error = client(&server)
        .guest_tasks("korriban", 320)
        .await
        .unwrap_err();
    assert!(error.to_string().contains("repeated a page"), "{error}");
}

#[tokio::test]
async fn an_archived_token_task_is_its_whole_principal() {
    let server = MockServer::start().await;
    Mock::given(method("GET"))
        .and(path("/api2/json/nodes/korriban/tasks"))
        .respond_with(data(json!([
            // Archived: pve-manager splits a token's owner (PVE/API2/Tasks.pm, convert_token_task).
            { "upid": "UPID:korriban:1:2:3:vzcreate:320:octopushy@pve!writer:", "type": "vzcreate", "id": "320",
              "user": "octopushy@pve", "tokenid": "writer", "status": "OK", "starttime": 3 },
            // Active: the whole principal in user.
            { "upid": "UPID:korriban:1:2:4:vzstart:320:octopushy@pve!writer:", "type": "vzstart", "id": "320",
              "user": "octopushy@pve!writer", "starttime": 4 },
            { "upid": "UPID:korriban:1:2:5:vzstart:320:octopushy@pve:", "type": "vzstart", "id": "320",
              "user": "octopushy@pve", "starttime": 5 }
        ])))
        .mount(&server)
        .await;
    let tasks = client(&server).guest_tasks("korriban", 320).await.unwrap();
    let principals: Vec<_> = tasks.iter().map(|t| t.principal()).collect();
    assert_eq!(
        principals,
        [
            Some("octopushy@pve!writer".to_owned()),
            Some("octopushy@pve!writer".to_owned()),
            Some("octopushy@pve".to_owned())
        ]
    );
}
