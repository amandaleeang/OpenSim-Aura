# HG friendship: discussion notes

Saved from the 2026-08-29 analysis (no code changes in that thread).
Full technical design: [DESIGN.md](DESIGN.md). Review of that draft: [REVIEW.md](REVIEW.md).

## Locked product decisions

Treat these as final.

1. **In-world Accept is final.** If B clicks Accept and we have B’s live traveling session (`SessionID` + `ServiceSessionID` / `VerifyAgent`), both homes complete immediately. No second “please confirm when you get home.”
2. **Offer fails closed if HomeB is unreachable.** A local popup without a pending row on B’s home is not an offer. Tell A it failed. Same rule as IM when HomeB cannot be contacted.
3. **Do not invent a third locate system.** Reuse the IM/profile split:
   - **Identity (HomeURI)** from CreatorData, UserManagement, agent circuit, or requester-home `get_uui`.
   - **Location** is HomeB’s job: presence, then `UserAgentService.LocateUser`, then (for IM) offline store. Friendship pending is the analogue of offline IM: stored at HomeB no matter where B is.
4. **Secret is minted at offer time** when HomeB writes the pending row (8-hex suffix on the UUI), not at accept.

## The insight that ties profile, IM, and friends

The viewer never has HomeURI. Profile, IM, and Add Friend all send **UUID only**.

Those last two look like the same IM channel. They are not:

- Chat (`MessageFromAgent`) goes through `InstantMessageModule` → `HGMessageTransferModule`.
- Friend offer (`FriendshipOffered`) is swallowed by `FriendsModule` / `HGFriendsModule` and never uses the IM locator.

That fork is why chat can work and the friend request still vanishes.

### Object creator → profile → IM (the path that already works)

B is not located when A looks at the prim. The prim already carries B’s home as **CreatorData** (`HomeURI;First Last` + `CreatorID`). `UserManagement.AddCreatorUser` seeds the cache. After that this sim can answer “who is UUID B?” without B being online.

**Profile:** resolve HomeURI (UserManagement, else A’s home `get_uui(A,B)`), then JSON-RPC to B’s profile host. HomeURI fallback when `ProfileServerURI` is missing.

**IM locate:** A’s sim only needs HomeB. Stamp A as a UUI on the wire. Send to HomeB. HomeB finds B via presence, `LocateUser` (traveler), or offline IM. If HomeB is down, IM fails — fail closed.

**Friends today:** if B is in the same sim, a local popup “succeeds” and neither home is written. If B is not here, `FriendsServerURI` has **no HomeURI fallback** (unlike IM/profile), so the offer often cannot even find an endpoint.

Friendship should reuse the same split: resolve HomeB like profile/IM, write pending on HomeB like offline IM, fail if HomeB does not ack.

## Scenario matrix (what actually happens today)

| # | Where A and B are | Offer | Accept / later |
|---|---|---|---|
| 1 | Both HG visitors, **same** local sim | Local popup. `StoreBackwards` skipped (A foreign). **Neither home called.** | `NewFriendship` with session if both circuits exist. If `FriendsServerURI` missing or `VerifyAgent` fails: **nothing at home**. If it works: flags=0 pending, must confirm **again** at home. |
| 2 | Both in Home A (A local, B visitor) | A→B writes UUID reverse pending on HomeA. B→A skips `StoreBackwards`. | HomeA may complete; HomeB often only pending. Desync if NewFriendship fails. |
| 3 | Same foreign grid, **different sims** | Other sim `/friends` IM named `"Unknown"`; no home pending. | Accepter has no other circuit → empty UUI → parse fail. **Broken.** |
| 4 | Different foreign grids | Needs UserManagement FriendsServerURI; name rewrite `@home` only if A is local; https often dropped. | Same UUI/home holes. |
| 5 | A at HomeA, B at HomeB | Home-to-home `ValidateFriendshipOffered` uses broken `GetFriends(string)`. **Dead since 2022.** | |
| 6 | Both local, same grid | Works. HG code not involved. | |
| 7 | Same home grid, both visiting elsewhere | 2016 `DeletePreviousHGRelations` was meant to convert HG pending → local UUID friends. If pending never arrived, this never runs. Second loop reads `a1` twice (bug). | |

## Code findings (verified)

Canonical stored form: `uuid;HomeURI;First Last[;secret]`. `TheirFlags == -1` (no converse row) = outstanding offer, replayed at login.

1. `FriendsService.GetFriends(string)` (commit `fd7d0128fe`, 2022) parses `i.Friend` **before** assigning `d.Friend` → drops every row. `ValidateFriendshipOffered` uses this overload.
2. `HGFriendsModule.StoreBackwards` is a no-op when the requester is foreign. A’s home has no anti-spam reverse pending.
3. Same-sim `LocalFriendshipOffered` returns success; homes are never contacted.
4. `StoreFriendships` both-foreigners builds UUI only from circuits **in this sim**. Agent-local/friend-absent already falls back to UserManagement; both-foreigners does not.
5. `ServiceURLs["FriendsServerURI"]` indexer throws if missing. `GetUserServerURL` HomeURI fallback is only for `HomeURI` / `IMServerURI`, not `FriendsServerURI`.
6. `/hgfriends` `friendship_offered` is unauthenticated; `FromName` is caller-controlled.
7. `ProcessFriendshipOffered` forces `http://` from display name; `new UserAgentServiceConnector` is never null, so https fallback is dead.
8. `HGFriendsServerPostHandler.DeleteFriendship`: `TryGetValue("SECRET") \|\| tmpObj is null` — **always** returns false. HG unfriend over `/hgfriends` is dead.
9. `HGFriendsModule.Delete` binds `ParseFullUniversalUserIdentifier` “url” to lastname; unfriend POST goes to a non-URI.
10. `DeletePreviousHGRelations` second loop uses `GetFriendsFromCache(a1)` again instead of `a2`.
11. `NewFriendship(verified=true)` stores flags=0 and returns false if any row already exists — so offer-time pending **blocks** accept completion.
12. `FriendsSimConnector.Call` is FireAndForget and always returns true. `HGFriendsService.FriendshipOffered` FireAndForgets validation and returns true if the account exists. Neither can fail closed.
13. `HGFriendsService.ForwardToSim` skips travelers (`RegionID == 0`). IM uses `LocateUser` here; friends do not.
14. `FriendsSimpleRequestHandler` ignores `FromName` and looks up local UserAccount → HG popups named `"Unknown"`.

## How identity is shared today

| Source | When | Quality |
|---|---|---|
| `AgentCircuitData.ServiceURLs["HomeURI"]` | Other avatar is root on **this** sim | Best |
| `FriendsServerURI` on the circuit | Same, if home published `SRV_FriendsServerURI` | Needed for `/hgfriends`; no fallback in `StoreFriendships` |
| UserManagement cache | CreatorData, previous visit, previous IM, friends list | Good if they have met this sim |
| Display name `First.Last @host:port` | Visitor naming | Lossy; rebuilt as `http://host` |
| Requester-home `get_uui(A,B)` | A’s home knows B as local, friend, or GridUser IM contact | How profile/IM find unknown targets |
| Presence / other sim | Same-grid different region | Local users; visitors on another simulator often invisible |

`get_uui` on a home: local account → `uuid;thisGrid;name`; else friends-list UUI (secret stripped); else GridUser UUI (IM contacts / visitors); else empty.

Session proof: circuit `SessionID` + `ServiceSessionID` (`gatekeeperURI;random`). Home `VerifyAgent` compares to traveling-agent `ServiceToken`. Used on `newfriendship` today, **not** on `friendship_offered`.

## Target procedure (agreed direction)

Visited sim is messenger. Homes own the rows.

**Offer:** resolve B’s HomeURI (UM → `get_uui` → fail) → session-prove A → reverse pending on HomeA → sync persist on HomeB (mint secret) → **fail if HomeB unreachable** → popup is UX only (HomeB locate, or local if B is here).

**Accept:** session-prove B → HomeB upgrades pending to flags=1 both ways **with the same secret** → HomeB notifies HomeA → no second confirm. If B never clicked, pending stays and login still replays `TheirFlags == -1`.

**Auth:** offer requires A’s session; HomeB calls A’s home `VerifyAgent`. Do not trust CreatorData / guessed host alone.

## PR plan (final, from DESIGN.md)

1. Protocol-neutral bugfixes (`GetFriends(string)`, SECRET always-false, Delete out-params, `DeletePreviousHGRelations`, https, FriendsServerURI HomeURI fallback, NewFriendship Result parse, FromName on `/friends`). **No `StoreBackwards` or `NewFriendship` flags change.**
2. Shared identity helper (HomeURI / UUI / `get_uui` / `RememberContact`).
3. **Home-canonical offer and accept-completes in one merge.** Flag `HomeCanonicalOffers` defaults **false** for one release. Traveler popup via HG IM locate-then-forward (2s timeout). HomeB orchestrates accept; rollback only if this call upgraded.
4. Logging polish / operator docs / manual matrix.

Reviewed to 0 open issues. [REVIEW.md](REVIEW.md) is the approve pass. [DESIGN.md](DESIGN.md) is the implementation spec.

## What we explicitly are not doing (this thread)

- No production code changes until the plan is agreed.
- No Friends table schema change.
- No viewer protocol change.
- No unifying IM, profile, and friends into one module — share helpers only.
