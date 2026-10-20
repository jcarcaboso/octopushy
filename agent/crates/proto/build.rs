//! Compiles the shared agent contract with protox (no `protoc` needed) and feeds
//! the descriptors to tonic's prost code generator.

use std::path::PathBuf;

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let manifest_dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR")?);
    let contracts = manifest_dir.join("../../../contracts");
    let proto = contracts.join("agent/v1/agent.proto");
    println!("cargo:rerun-if-changed={}", proto.display());

    let descriptors = protox::compile([&proto], [&contracts])?;
    tonic_prost_build::configure()
        // `rpc Connect` would collide with the generated `AgentChannelClient::connect`
        // constructor, so the transport helper is not generated; clients are built
        // with `AgentChannelClient::new(channel)` instead.
        .build_transport(false)
        .build_client(true)
        .build_server(true)
        .compile_fds(descriptors)?;
    Ok(())
}
