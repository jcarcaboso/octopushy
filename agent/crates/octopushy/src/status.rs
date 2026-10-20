//! `octopushy agent status`: what is stored in the state directory.

use std::time::SystemTime;

use anyhow::Result;

use crate::identity;
use crate::outbox::Outbox;
use crate::paths::StatePaths;
use crate::renewal::humantime;

pub fn print(paths: &StatePaths) -> Result<()> {
    let id = identity::load(paths)?;
    let now = SystemTime::now();
    println!("state directory: {}", paths.root.display());
    println!("agent id:        {}", id.record.agent_id);
    println!("name:            {}", id.record.name);
    println!("server:          {}", id.record.server_url);
    println!(
        "certificate:     valid until {} (unix), renews after {}{}",
        humantime(id.cert.not_after),
        humantime(id.cert.renew_after()),
        if id.cert.is_expired(now) {
            " - EXPIRED, enroll again"
        } else if id.cert.needs_renewal(now) {
            " - renewal due"
        } else {
            ""
        }
    );
    if paths.outbox().exists() {
        let pending = Outbox::open(paths.outbox())?.list()?;
        println!("unacknowledged:  {} results", pending.len());
    }
    Ok(())
}
