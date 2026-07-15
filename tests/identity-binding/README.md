# Player identity binding harness

This executable verifies the durable `platformSubject <-> current-world
PlayerUID` authorization boundary in `extraction-commerce.db`.

It covers separate Steam accounts and wallets, both directions of the SQLite
uniqueness constraint, refusal to change PlayerUID within a week, required
rebinding after rollover, display-name changes, history, active-binding lookup,
and persistence across a repository restart. It does not claim Steam OpenID,
session revocation, ban integration, or abnormal-login auditing.
