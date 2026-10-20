//! A concurrency limit that can change while attempts run (the server sends a
//! new `max_concurrency` on every connect).

use std::sync::{Arc, Mutex};

use tokio::sync::Notify;

struct State {
    limit: usize,
    active: usize,
}

pub struct Limiter {
    state: Mutex<State>,
    changed: Notify,
}

/// Holds one slot until dropped.
pub struct Permit {
    limiter: Arc<Limiter>,
}

impl Limiter {
    pub fn new(limit: usize) -> Arc<Self> {
        Arc::new(Self {
            state: Mutex::new(State {
                limit: limit.max(1),
                active: 0,
            }),
            changed: Notify::new(),
        })
    }

    /// Sets the limit (at least 1). Running attempts keep their slots when it
    /// shrinks; new ones wait until enough have finished.
    pub fn set_limit(&self, limit: usize) {
        self.state.lock().unwrap().limit = limit.max(1);
        self.changed.notify_waiters();
    }

    pub fn limit(&self) -> usize {
        self.state.lock().unwrap().limit
    }

    pub async fn acquire(self: &Arc<Self>) -> Permit {
        loop {
            let notified = self.changed.notified();
            tokio::pin!(notified);
            notified.as_mut().enable();
            {
                let mut s = self.state.lock().unwrap();
                if s.active < s.limit {
                    s.active += 1;
                    return Permit {
                        limiter: self.clone(),
                    };
                }
            }
            notified.await;
        }
    }
}

impl Drop for Permit {
    fn drop(&mut self) {
        self.limiter.state.lock().unwrap().active -= 1;
        self.limiter.changed.notify_waiters();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;

    #[tokio::test]
    async fn limits_and_grows() {
        let l = Limiter::new(1);
        let p1 = l.acquire().await;
        let l2 = l.clone();
        let waiter = tokio::spawn(async move { l2.acquire().await });
        tokio::time::sleep(Duration::from_millis(20)).await;
        assert!(!waiter.is_finished());
        l.set_limit(2);
        let p2 = tokio::time::timeout(Duration::from_secs(1), waiter)
            .await
            .unwrap()
            .unwrap();
        drop((p1, p2));
        let _a = l.acquire().await;
        let _b = l.acquire().await;
    }

    #[tokio::test]
    async fn releases_on_drop() {
        let l = Limiter::new(1);
        let p = l.acquire().await;
        let l2 = l.clone();
        let waiter = tokio::spawn(async move { l2.acquire().await });
        drop(p);
        tokio::time::timeout(Duration::from_secs(1), waiter)
            .await
            .unwrap()
            .unwrap();
    }
}
