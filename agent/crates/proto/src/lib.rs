//! Generated code for `contracts/agent/v1/agent.proto`.
//!
//! The proto file lives outside the agent workspace because the .NET backend
//! generates its side from the same file. It is compiled with `protox`, a pure-Rust
//! protobuf compiler, so building needs no `protoc`.

#![allow(clippy::all, clippy::pedantic)]

pub mod agent {
    pub mod v1 {
        tonic::include_proto!("octopushy.agent.v1");
    }
}

pub use agent::v1;
