## Design Document Review: Hypergrid Friendship Offer/Accept: Home-Canonical Pending

### Summary
Approve. The four remaining issues from the last pass are addressed in the document: host match is hostname-only (not `OSHHTPHost.Equals`), `already` is idempotent success with rollback only after this-call upgrade, traveler popup uses a 2s IM HTTP timeout (not the 10s helper), and `/hgfriends` HTTP is foreign-home only. An engineer can implement from this design.

### Previously addressed (verified in this pass; do not re-litigate)

**Host match.** Decision 11/auth table and offer step 5 specify `HomeHostsMatch`: case-insensitive `OSHHTPHost.Host`; ports compared only if **both** original strings have an explicit non-default port. Required PR3 test: `FromName=First.Last@grid.example` + `FromHomeURI=https://grid.example/` accepts. That is the 80-vs-443 trap (`GridInfo.cs` 339–346). `HasExplicitNonDefaultPort` treating 80/443 as default is enough even if `ResolveSenderHomeURI` returns a URI that includes `:80`.

**Accept idempotency.** `TryCompleteLocal` returns `Upgraded` / `Already` / `NoPending`. `Already` → `RESULT=true`, no flags=1 delete, best-effort HomeA notify, **never rollback**. Rollback only if this call upgraded and HomeA then failed. Sim treats `already`/`upgraded` as success. PR3 tests cover second complete and already+HomeA-fail.

**2s popup.** Do not call `InstantMessageServiceConnector.SendInstantMessage` as-is (`GetNewGlobalHttpClient(10000)`). PR3 adds a timeout overload; friendship popup uses 2000ms. Timeout ⇒ `Delivered=false`, `RESULT=true`, pending stored. Same `[Messaging] MessageKey` as `HGInstantMessageService`; region `MessageTransferModule` is the checker.

**This-grid vs foreign.** Decision 14 and offer steps 3–4: HTTP `/hgfriends` only to a **foreign** home. This-grid home uses this grid’s Friends/HGFriends path (standalone in-process; grid = this Robust connector or `/hgfriends` on this Robust). Canonical secret is minted on HomeB’s service, not on a visited sim that is not HomeB.

**Earlier blockers (still met).** Offer persist and accept-completes are one PR; flag defaults false; traveler popup is HG IM locate-then-forward, not POST `/friends` to the gatekeeper URI; offer checklist does not call `base.ForwardFriendshipOffer` first.

### Special-attention checklist
| Item | Status |
|---|---|
| PR3+PR4 one merge so the flag cannot persist pending Accept cannot complete | **Met.** |
| Traveler popup is HG IM locate-then-forward, not POST `/friends` to gatekeeper | **Met.** |
| Offer control flow no longer local-first | **Met.** |
| Accept: HomeB orchestrates, HomeA fail → retry then rollback | **Met.** Rollback only if this call upgraded; `already` is success. |
| Feature flag defaults false for one release | **Met.** |
| Delivered vs 2s timeout consistent | **Met.** 2s IM client overload; timeout = Delivered=false, RESULT=true. |
| FromName vs FromHomeURI host match is a real check | **Met.** Hostname match; not `OSHHTPHost.Equals`; 80 vs 443 test. |
| This-grid vs foreign HTTP wording | **Met.** |

### Strengths
- Root causes match the code (GetFriends(string), StoreBackwards no-op, both-foreigners circuits, verified flags=0, SECRET always-false, Delete lastname-bind, `http://` + dead null connector, FireAndForget, NewFriendship Result parse, LocateUser gatekeeper URI).
- Canonical invariant is the right split; IM is popup transport only; friends table stays the source of pending.
- Mixed-version HomeA vs HomeB is an operator-actionable callout with a safe default-false flag.
- PR plan is ordered and independently reviewable except the intentional offer+accept merge; tests sit in the introducing PR.
- `HomeHostsMatch`, `TryCompleteLocal` reasons, and the 2s IM overload are specific enough to implement without re-deriving the review.
