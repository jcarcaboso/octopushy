use base64::Engine;
use octopushy::{managed_identity, paths::StatePaths};
use serde_json::json;

const GENERATION: &str = "884d09b4-0934-43a4-bffb-d2f7977bc01c";

#[test]
fn preparation_is_generation_bound_idempotent_and_never_exports_the_key() {
    let dir = tempfile::tempdir().unwrap();
    let paths = StatePaths::new(dir.path());
    let first = managed_identity::prepare(&paths, GENERATION).unwrap();
    let second = managed_identity::prepare(&paths, GENERATION).unwrap();
    assert_eq!(first, second);
    assert_eq!(first["protocolVersion"], 1);
    assert_eq!(first["generation"], GENERATION);
    assert_eq!(first.as_object().unwrap().len(), 4);
    assert!(!first.to_string().contains("PRIVATE"));
    assert!(managed_identity::prepare(&paths, "884d09b4-0934-43a4-bffb-d2f7977bc01d").is_err());
    assert!(!paths.agent_json().exists());
}

#[test]
fn installation_checks_key_ca_and_identity_and_exact_retries_are_safe() {
    let dir = tempfile::tempdir().unwrap();
    let paths = StatePaths::new(dir.path());
    let report = managed_identity::prepare(&paths, GENERATION).unwrap();
    let csr = base64::engine::general_purpose::STANDARD
        .decode(report["csrDer"].as_str().unwrap())
        .unwrap();
    let ca_key = rcgen::KeyPair::generate().unwrap();
    let mut ca_params = rcgen::CertificateParams::default();
    ca_params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    let ca = ca_params.self_signed(&ca_key).unwrap();
    let issuer = rcgen::Issuer::new(ca_params, ca_key);
    let id = "2e21c5dc-047b-4023-a07d-7ce22eb822a3";
    let mut request = rcgen::CertificateSigningRequestParams::from_der(&csr.into()).unwrap();
    request.params.distinguished_name = rcgen::DistinguishedName::new();
    request
        .params
        .distinguished_name
        .push(rcgen::DnType::CommonName, id);
    request.params.extended_key_usages = vec![rcgen::ExtendedKeyUsagePurpose::ClientAuth];
    let cert = request.signed_by(&issuer).unwrap();
    let payload = json!({"protocolVersion":1,"generation":GENERATION,"agentId":id,"name":"worker-1",
        "serverUrl":"https://controller.example:8443","certificatePem":cert.pem(),"caCertificatePem":ca.pem()});
    let mut invalid = payload.clone();
    invalid["agentId"] = json!(GENERATION);
    assert!(managed_identity::install(&paths, GENERATION, &invalid.to_string()).is_err());
    assert!(!paths.agent_json().exists());
    let foreign_key = rcgen::KeyPair::generate().unwrap();
    let mut foreign_params = rcgen::CertificateParams::default();
    foreign_params
        .distinguished_name
        .push(rcgen::DnType::CommonName, id);
    foreign_params.extended_key_usages = vec![rcgen::ExtendedKeyUsagePurpose::ClientAuth];
    let foreign_cert = foreign_params.signed_by(&foreign_key, &issuer).unwrap();
    invalid = payload.clone();
    invalid["certificatePem"] = json!(foreign_cert.pem());
    assert!(managed_identity::install(&paths, GENERATION, &invalid.to_string()).is_err());
    let other_ca_key = rcgen::KeyPair::generate().unwrap();
    let mut other_ca_params = rcgen::CertificateParams::default();
    other_ca_params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    invalid = payload.clone();
    invalid["caCertificatePem"] = json!(other_ca_params.self_signed(&other_ca_key).unwrap().pem());
    assert!(managed_identity::install(&paths, GENERATION, &invalid.to_string()).is_err());
    std::fs::write(paths.key(), "foreign existing private identity").unwrap();
    assert!(managed_identity::install(&paths, GENERATION, &payload.to_string()).is_err());
    assert_eq!(
        std::fs::read_to_string(paths.key()).unwrap(),
        "foreign existing private identity"
    );
    std::fs::remove_file(paths.key()).unwrap();
    managed_identity::install(&paths, GENERATION, &payload.to_string()).unwrap();
    let before = std::fs::read(paths.agent_json()).unwrap();
    managed_identity::install(&paths, GENERATION, &payload.to_string()).unwrap();
    assert_eq!(before, std::fs::read(paths.agent_json()).unwrap());
    let mut changed = payload;
    changed["serverUrl"] = json!("https://elsewhere.example");
    assert!(managed_identity::install(&paths, GENERATION, &changed.to_string()).is_err());
}

#[test]
fn cli_prepare_emits_only_public_json_and_binds_the_generation() {
    let dir = tempfile::tempdir().unwrap();
    let invoke = |generation: &str| {
        std::process::Command::new(env!("CARGO_BIN_EXE_octopushy"))
            .args([
                "agent",
                "managed-identity",
                "prepare",
                "--generation",
                generation,
                "--state-dir",
            ])
            .arg(dir.path())
            .output()
            .unwrap()
    };
    let first = invoke(GENERATION);
    assert!(
        first.status.success(),
        "{}",
        String::from_utf8_lossy(&first.stderr)
    );
    let report: serde_json::Value = serde_json::from_slice(&first.stdout).unwrap();
    assert_eq!(report.as_object().unwrap().len(), 4);
    assert!(!String::from_utf8_lossy(&first.stdout).contains("PRIVATE KEY"));
    assert_eq!(first.stdout, invoke(GENERATION).stdout);
    assert!(
        !invoke("884d09b4-0934-43a4-bffb-d2f7977bc01d")
            .status
            .success()
    );
}
