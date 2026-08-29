# Design summary: Hypergrid friendship home-canonical pending

Wrote `/tmp/grok-opensim/grok-design-doc-75847633.md` (revised after review).

## What it is

Technical design for making HG friendship offer/accept reliable in OpenSim-Aura. Canonical pending and accepted state lives at each avatar's HOME. The visited sim is only a meeting place and messenger. Identity/locate reuse the existing HG IM and profile split (`get_uui`, UserManagement, `LocateUser`). Traveler **popup** uses HG IM locate-then-forward (`grid_instant_message` to the gatekeeper URI), not POST `/friends` to that URI. In-world Accept with a live traveling session completes both homes immediately (no second confirm). Offers fail closed if HomeB cannot be reached.

## Verified in code (not just the prompt)

- `FriendsService.GetFriends(string)` parses empty `i.Friend` and drops every row; `ValidateFriendshipOffered` uses this overload.
- `HGFriendsModule.StoreBackwards` no-ops for foreign requesters.
- `StoreFriendships` both-foreigners needs circuits on *this* sim; indexer `ServiceURLs["FriendsServerURI"]` throws; no HomeURI fallback (unlike IM/profile).
- `NewFriendship(verified=true)` stores flags=0 and returns false if a pending row already exists — accept cannot complete offer-time pending.
- `DeleteFriendship` SECRET check **always returns false** (`TryGetValue || tmpObj is null`); service never called.
- `DeletePreviousHGRelations` second loop reads cache for `a1` again.
- `HGFriendsModule.Delete` binds `ParseFullUniversalUserIdentifier` "url" to lastname, so unfriend POSTs to a non-URI.
- `ProcessFriendshipOffered` forces `http://`; `new UserAgentServiceConnector` is never null so https fallback is dead.
- `FriendshipOffered` FireAndForgets validation; `FriendsSimConnector.Call` always returns true. Neither can fail closed.
- `HGFriendsServicesConnector.NewFriendship` parses `<Result>Success</Result>` with `Boolean.TryParse` → always false.
- `LocateUser` returns gatekeeper URI (`CreateTravelInfo` copies gatekeeper `ServerURI`). `/friends` is a region handler.
- `[HGFriendsService] UserAgentService` is already in `Robust.HG.ini.example`; constructor ignores it.
- Chat IMs go through `HGMessageTransferModule`; friendship does not. Incoming FriendshipOffered IM is still deliverable if `HGFriendsModule` listens to multicast `OnIncomingInstantMessage`.

## Locked + extra decisions

Locked: in-world Accept is final; fail closed if HomeB unreachable; reuse IM/profile identity+locate; secret minted at HomeB at offer time.

Added:

- Keep `/hgfriends` query-string and add fields.
- **`HomeCanonicalOffers` default false for one release** (old HomeA fail-closed is a sharp cutover). Incoming Robust path always upgrades pending.
- Outgoing old **HomeB**: FromName degraded success if HTTP succeeds. Outgoing old **HomeA**: fail closed (`store_reverse_pending` unknown). Incoming old: name-parse + Validate.
- Offer persist and accept-completes **ship in the same PR**.
- Popup: persist, then 2s home `/friends` or IM locate (**2s HTTP client, not the 10s `SendInstantMessage` helper**), then `Delivered`. Timeout ⇒ Delivered=false, pending stored. Same `[Messaging] MessageKey` as HG IM.
- HomeB orchestrates accept; UUID-prefix pending lookup; reasons `upgraded` / `already` / `no_pending` / `homea_failed`. **`already` is idempotent success.** HomeA fail → retry once → **rollback HomeB only if this call upgraded** flags=0→1.
- HTTP `/hgfriends` only when that home is a **foreign grid**. This-grid home uses this grid’s Friends/HGFriends path (standalone in-process; grid = this Robust). Secret minted on HomeB’s service, not only on a visited sim that is not HomeB.
- Host anti-spoof: **hostname** case-insensitive; ports only if both have an explicit non-default port. **Not** `OSHHTPHost.Equals` (https `grid.example` is 443 vs FromName Port=80). `FromName` optional on the new path.

## PR plan (in the doc)

1. Protocol-neutral bugfixes (GetFriends TryParse prefix, SECRET always-false, DeletePreviousHGRelations, Delete out-params, https, FriendsServerURI fallback, NewFriendship Result parse, FromName on `/friends`). **No `StoreBackwards` change.** Tests in this PR.
2. Shared `HGIdentity` helper + `OpenSim.Region.CoreModules.csproj` Compile include. Tests in this PR.
3. **Home-canonical offer and accept-completes (one merge).** Flag default false. IM locate popup with 2s timeout overload. HomeB→HomeA rollback only if this call upgraded. Hostname host match + https default-port test. Tests in this PR.
4. Logging polish / operator docs / manual matrix.

No schema change. No implementation in this task.
