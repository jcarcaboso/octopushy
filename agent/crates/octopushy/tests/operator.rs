use std::io::{Read, Write};
use std::process::{Command, Stdio};

#[test]
fn operator_cli_is_separate_from_execution_agent_identity() {
    let output = Command::new(env!("CARGO_BIN_EXE_octopushy"))
        .args(["operator", "--help"])
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let help = String::from_utf8(output.stdout).unwrap();
    assert!(help.contains("login") && help.contains("request") && help.contains("logout"));
    assert!(!help.contains("managed-identity"));
}

const CLEARTEXT_WARNING: &str = "warning: the operator token travels unencrypted";

fn private_directory() -> tempfile::TempDir {
    use std::os::unix::fs::PermissionsExt;
    let directory = tempfile::tempdir().unwrap();
    std::fs::set_permissions(directory.path(), std::fs::Permissions::from_mode(0o700)).unwrap();
    directory
}

/// The command may refuse before it reads stdin, so a closed pipe is not a failure here.
fn operator(config: &std::path::Path, args: &[&str], input: &str) -> std::process::Output {
    let mut child = Command::new(env!("CARGO_BIN_EXE_octopushy"))
        .args(["operator", "--config", config.to_str().unwrap()])
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .unwrap();
    let mut stdin = child.stdin.take().unwrap();
    let _ = stdin.write_all(input.as_bytes());
    drop(stdin);
    child.wait_with_output().unwrap()
}

#[test]
fn operator_login_keeps_every_origin_check_on_both_schemes() {
    let directory = private_directory();
    let config = directory.path().join("credential.json");
    for server in [
        "ftp://controller",
        "controller:8081",
        "http://user:not-a-secret@controller:8081",
        "http://controller:8081/path",
        "http://controller:8081/?query",
        "http://controller:8081/#fragment",
        "https://user:not-a-secret@controller",
        "https://controller/path",
        "https://controller/?query",
        "https://controller/#fragment",
    ] {
        let output = operator(
            &config,
            &["login", "--server", server, "--token-stdin"],
            "synthetic-private-token\n",
        );
        assert!(!output.status.success(), "{server}");
        let error = String::from_utf8_lossy(&output.stderr);
        assert!(error.contains("origin without credentials"), "{error}");
        assert!(!error.contains("not-a-secret") && !error.contains("synthetic-private-token"));
        assert!(!error.contains(CLEARTEXT_WARNING), "{error}");
        assert!(!config.exists());
    }
}

#[test]
fn operator_login_refuses_a_ca_file_over_plain_http_before_contacting_the_controller() {
    let directory = private_directory();
    let config = directory.path().join("credential.json");
    let ca = directory.path().join("ca.pem");
    std::fs::write(&ca, "unused").unwrap();
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let origin = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
    let output = operator(
        &config,
        &[
            "login",
            "--server",
            &origin,
            "--token-stdin",
            "--ca-file",
            ca.to_str().unwrap(),
        ],
        "synthetic-private-token\n",
    );
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(
        error.contains("--ca-file") && error.contains("https://"),
        "{error}"
    );
    assert!(!error.contains("synthetic-private-token"));
    assert!(!config.exists());
    assert_eq!(
        listener.accept().unwrap_err().kind(),
        std::io::ErrorKind::WouldBlock
    );
}

#[test]
fn operator_refuses_a_saved_ca_on_a_plain_http_origin_before_sending_the_token() {
    use std::os::unix::fs::PermissionsExt;
    let directory = private_directory();
    let config = directory.path().join("credential.json");
    let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
    listener.set_nonblocking(true).unwrap();
    let token = format!("opy_op_{}_{}", "1".repeat(32), "a".repeat(64));
    std::fs::write(
        &config,
        serde_json::json!({
            "server": format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port()),
            "token": token,
            "caCertificatePem": "unused"
        })
        .to_string(),
    )
    .unwrap();
    std::fs::set_permissions(&config, std::fs::Permissions::from_mode(0o600)).unwrap();
    let output = operator(&config, &["whoami"], "");
    assert!(!output.status.success());
    let error = String::from_utf8_lossy(&output.stderr);
    assert!(error.contains("--ca-file"), "{error}");
    assert!(!error.contains(&token));
    assert_eq!(
        listener.accept().unwrap_err().kind(),
        std::io::ErrorKind::WouldBlock
    );
}

#[test]
fn operator_works_over_plain_http_and_warns_once_per_command_without_printing_the_token() {
    use std::{fs, net::TcpListener, os::unix::fs::MetadataExt};
    let directory = private_directory();
    let config = directory.path().join("credential.json");
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!("http://127.0.0.1:{}", listener.local_addr().unwrap().port());
    let token = format!("opy_op_{}_{}", "1".repeat(32), "a".repeat(64));
    let (expected, sent_origin) = (token.clone(), origin.clone());
    let server = std::thread::spawn(move || {
        for (path, status, body) in [
            (
                "GET /api/auth/state ",
                "200 OK",
                r#"{"authenticated":true,"username":"ops [CLI: assistant]"}"#,
            ),
            (
                "GET /api/auth/state ",
                "200 OK",
                r#"{"authenticated":true,"username":"ops [CLI: assistant]"}"#,
            ),
            (
                "POST /api/guests/guest-1/plan ",
                "200 OK",
                r#"{"runId":"run-1"}"#,
            ),
            ("GET /api/redirect ", "302 Found", ""),
            (
                "DELETE /api/auth/operator-tokens/self ",
                "204 No Content",
                "",
            ),
        ] {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = Vec::new();
            while !request.windows(4).any(|v| v == b"\r\n\r\n") {
                let mut buffer = [0u8; 1024];
                let count = stream.read(&mut buffer).unwrap();
                assert_ne!(count, 0);
                request.extend_from_slice(&buffer[..count]);
                assert!(request.len() < 16384);
            }
            let request = String::from_utf8(request).unwrap().to_lowercase();
            assert!(request.starts_with(&path.to_lowercase()), "{request}");
            assert!(request.contains(&format!("authorization: bearer {expected}")));
            assert!(request.contains(&format!("origin: {sent_origin}")));
            write!(stream, "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nLocation: http://127.0.0.1:1/never-follow\r\nConnection: close\r\n\r\n{body}", body.len()).unwrap();
            stream.flush().unwrap();
        }
    });
    let call = |args: &[&str], input: &str| {
        let output = operator(&config, args, input);
        let stderr = String::from_utf8_lossy(&output.stderr).into_owned();
        assert_eq!(stderr.matches(CLEARTEXT_WARNING).count(), 1, "{stderr}");
        assert!(stderr.contains(&origin), "{stderr}");
        assert!(!stderr.contains(&token));
        assert!(!String::from_utf8_lossy(&output.stdout).contains(&token));
        assert!(!String::from_utf8_lossy(&output.stdout).contains("warning"));
        output
    };
    let login = call(&["login", "--server", &origin, "--token-stdin"], &token);
    assert!(
        login.status.success(),
        "{}",
        String::from_utf8_lossy(&login.stderr)
    );
    assert_eq!(fs::metadata(&config).unwrap().mode() & 0o777, 0o600);
    let saved: serde_json::Value = serde_json::from_slice(&fs::read(&config).unwrap()).unwrap();
    assert_eq!(saved["server"], origin.as_str());
    assert!(saved["caCertificatePem"].is_null());
    let whoami = call(&["whoami"], "");
    assert!(whoami.status.success());
    let auth: serde_json::Value = serde_json::from_slice(&whoami.stdout).unwrap();
    assert_eq!(auth["username"], "ops [CLI: assistant]");
    assert!(
        call(
            &[
                "request",
                "/api/guests/guest-1/plan",
                "--method",
                "POST",
                "--json-stdin"
            ],
            "{}"
        )
        .status
        .success()
    );
    let redirected = call(&["request", "/api/redirect"], "");
    assert!(!redirected.status.success());
    assert!(String::from_utf8_lossy(&redirected.stderr).contains("HTTP 302"));
    assert!(call(&["logout"], "").status.success());
    assert!(!config.exists());
    server.join().unwrap();
}

#[test]
fn private_operator_login_reuses_its_grant_over_verified_tls_and_logout_revokes_it() {
    use std::{
        fs,
        net::TcpListener,
        os::unix::fs::{MetadataExt, PermissionsExt},
        sync::Arc,
    };
    let directory = tempfile::tempdir().unwrap();
    fs::set_permissions(directory.path(), fs::Permissions::from_mode(0o700)).unwrap();
    let config = directory.path().join("credential.json");
    let certificate = rcgen::generate_simple_self_signed(vec!["localhost".into()]).unwrap();
    let ca = directory.path().join("ca.pem");
    fs::write(&ca, certificate.cert.pem()).unwrap();
    let _ = rustls::crypto::ring::default_provider().install_default();
    let tls = rustls::ServerConfig::builder()
        .with_no_client_auth()
        .with_single_cert(
            vec![certificate.cert.der().clone()],
            rustls::pki_types::PrivatePkcs8KeyDer::from(certificate.signing_key.serialize_der())
                .into(),
        )
        .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let origin = format!(
        "https://localhost:{}",
        listener.local_addr().unwrap().port()
    );
    let token = format!("opy_op_{}_{}", "1".repeat(32), "a".repeat(64));
    let expected = token.clone();
    let server = std::thread::spawn(move || {
        for (path, status, body) in [
            (
                "GET /api/auth/state ",
                "200 OK",
                r#"{"authenticated":true,"username":"ops [CLI: assistant]"}"#,
            ),
            (
                "GET /api/auth/state ",
                "200 OK",
                r#"{"authenticated":true,"username":"ops [CLI: assistant]"}"#,
            ),
            (
                "POST /api/guests/guest-1/plan ",
                "200 OK",
                r#"{"runId":"run-1"}"#,
            ),
            ("PATCH /api/agents/agent-1 ", "204 No Content", ""),
            ("GET /api/redirect ", "302 Found", ""),
            (
                "DELETE /api/auth/operator-tokens/self ",
                "204 No Content",
                "",
            ),
        ] {
            let (socket, _) = listener.accept().unwrap();
            let connection = rustls::ServerConnection::new(Arc::new(tls.clone())).unwrap();
            let mut stream = rustls::StreamOwned::new(connection, socket);
            let mut request = Vec::new();
            while !request.windows(4).any(|v| v == b"\r\n\r\n") {
                let mut buffer = [0u8; 1024];
                let count = stream.read(&mut buffer).unwrap();
                assert_ne!(count, 0);
                request.extend_from_slice(&buffer[..count]);
                assert!(request.len() < 16384);
            }
            let request = String::from_utf8(request).unwrap();
            assert!(request.starts_with(path), "{request}");
            assert!(
                request
                    .to_lowercase()
                    .contains(&format!("authorization: bearer {expected}"))
            );
            write!(stream, "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nLocation: https://localhost:1/never-follow\r\nConnection: close\r\n\r\n{body}", body.len()).unwrap();
            stream.flush().unwrap();
        }
    });
    let call = |args: &[&str], input: Option<&str>| {
        let mut child = Command::new(env!("CARGO_BIN_EXE_octopushy"))
            .args(["operator", "--config", config.to_str().unwrap()])
            .args(args)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::piped())
            .spawn()
            .unwrap();
        if let Some(input) = input {
            child
                .stdin
                .take()
                .unwrap()
                .write_all(input.as_bytes())
                .unwrap();
        }
        drop(child.stdin.take());
        let output = child.wait_with_output().unwrap();
        assert!(!String::from_utf8_lossy(&output.stdout).contains(&token));
        assert!(!String::from_utf8_lossy(&output.stderr).contains(&token));
        assert!(!String::from_utf8_lossy(&output.stderr).contains(CLEARTEXT_WARNING));
        output
    };
    let login = call(
        &[
            "login",
            "--server",
            &origin,
            "--token-stdin",
            "--ca-file",
            ca.to_str().unwrap(),
        ],
        Some(&token),
    );
    assert!(
        login.status.success(),
        "{}",
        String::from_utf8_lossy(&login.stderr)
    );
    assert_eq!(fs::metadata(&config).unwrap().mode() & 0o777, 0o600);
    assert!(call(&["whoami"], None).status.success());
    assert!(
        call(
            &[
                "request",
                "/api/guests/guest-1/plan",
                "--method",
                "POST",
                "--json-stdin"
            ],
            Some("{}")
        )
        .status
        .success()
    );
    assert!(
        call(
            &[
                "request",
                "/api/agents/agent-1",
                "--method",
                "PATCH",
                "--json-stdin"
            ],
            Some(r#"{"draining":false}"#)
        )
        .status
        .success()
    );
    assert!(!call(&["request", "/api/redirect"], None).status.success());
    assert!(call(&["logout"], None).status.success());
    assert!(!config.exists());
    server.join().unwrap();
}

#[test]
fn operator_refuses_shared_files_links_and_paths_outside_the_api_without_leaking_the_token() {
    use std::{
        fs,
        os::unix::fs::{PermissionsExt, symlink},
    };
    let directory = tempfile::tempdir().unwrap();
    fs::set_permissions(directory.path(), fs::Permissions::from_mode(0o700)).unwrap();
    let config = directory.path().join("credential.json");
    let token = format!("opy_op_{}_{}", "1".repeat(32), "a".repeat(64));
    fs::write(
        &config,
        serde_json::json!({
            "server":"https://localhost:1", "token":token, "caCertificatePem":null
        })
        .to_string(),
    )
    .unwrap();
    let refused = |path: &std::path::Path, api_path: &str| {
        let output = Command::new(env!("CARGO_BIN_EXE_octopushy"))
            .args([
                "operator",
                "--config",
                path.to_str().unwrap(),
                "request",
                api_path,
            ])
            .output()
            .unwrap();
        assert!(!output.status.success());
        assert!(!String::from_utf8_lossy(&output.stdout).contains(&token));
        assert!(!String::from_utf8_lossy(&output.stderr).contains(&token));
        String::from_utf8(output.stderr).unwrap()
    };
    fs::set_permissions(&config, fs::Permissions::from_mode(0o644)).unwrap();
    assert!(refused(&config, "/api/inventory").contains("private"));
    fs::set_permissions(&config, fs::Permissions::from_mode(0o600)).unwrap();
    let link = directory.path().join("link");
    symlink(&config, &link).unwrap();
    assert!(refused(&link, "/api/inventory").contains("private"));
    fs::remove_file(&link).unwrap();
    fs::hard_link(&config, &link).unwrap();
    assert!(refused(&config, "/api/inventory").contains("private"));
    fs::remove_file(&link).unwrap();
    assert!(refused(&config, "/api/../../outside").contains("outside"));
    assert!(config.exists());
}
