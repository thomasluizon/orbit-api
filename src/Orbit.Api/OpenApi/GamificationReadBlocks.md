# Gamification read blocks

The API must deploy before a consumer advertises the `gamification` part kind.
Tracks thomasluizon/orbit-tickets#1631 and the carrying consumer in
thomasluizon/orbit-tickets#1619. The consumer can pin any published OpenAPI
artifact by producer commit and catalog identity as described in [README.md](README.md).

`get_gamification_overview` selects profile, achievement catalogue and streak
independently. A successful read with at least one selected source projects a
read block. Projection preserves the existing `streak` kind whenever profile
and streak are both present. Every other nonempty selection uses the append-only
`gamification` kind.

| include_profile | include_achievements | include_streak | Kind |
| --- | --- | --- | --- |
| true | false | false | gamification |
| false | true | false | gamification |
| false | false | true | gamification |
| true | true | false | gamification |
| false | true | true | gamification |
| true | false | true | streak |
| true | true | true | streak |
| false | false | false | No block: no source selected |

Omitted include flags retain their existing true defaults. A selected source
failure still fails the operation; it does not produce a partial success card.

## Negotiation and wire shape

The existing capability mechanism requires `clientContext.supportsParts=true`
and the exact `gamification` string in `clientContext.partKinds`. There is no
new capability boolean. `astra_status_blocks_disabled` disables this kind along
with the existing status cards. `supportsStreakCard` alone does not enable it.
Clients without the new capability receive no new card or top-level field.
Combined reads retain their existing streak capability requirements and fields.

A carrying client receives a chat block part with `kind: "gamification"` and
this card schema:

| Card field | Contract |
| --- | --- |
| state | Original `GamificationOverviewPayload`: nullable profile, achievements and streak |
| surfaceId | `progress` |
| read | Existing `ReadBlockMetadata`, including scope, revision, queryId and refreshToken |

The three `state` fields use the published `GamificationProfileResponse`,
`AchievementsResponse` and `StreakInfoResponse` contracts. An unselected source
is null, never an invented zero or placeholder. Achievement-only results have
null profile and streak. Profile-only results have null achievements and streak.
The profile's own returned streak totals and achievement fields remain part of
that source; they do not imply the separate streak or catalogue query ran.
When both profile and catalogue are selected, the tool keeps the catalogue only
in `state.achievements`, preserving its existing deduplication behavior.

Consumers render level and XP from a present profile, achievement status and
progress from the returned catalogue, and freeze or repair details only from a
present streak. The card adds no mandatory level, XP or streak summary figures.
The full returned collections are preserved without a six-achievement display cap.
The existing `streak` card also retains its complete original payload in `state`.

## Scope and refresh

`read.scope` contains the resolved original arguments, including each supplied
include flag and the absolute `as_of` date resolved through the user's timezone.
The date retains the tool's existing semantics: the streak query evaluates that
date, while profile and achievements return their source queries' current values.
It does not promise historical profile or achievement snapshots.

`POST /api/chat/reads/refresh` accepts the existing `token` and `clientContext`
request. Use the original `read.refreshToken`; do not substitute a broader query.
Refresh reruns the same selected sources and absolute as-of scope, retains the
query ID, returns the original kind, and updates the revision from the returned
source payload. A profile-only refresh still has no separate streak or catalogue.
Advancing the user's local day does not advance the query's as-of date.

Refresh requires the same explicit part-kind capability and enabled status-block
flag. Existing token ownership, expiry, source errors and policy checks apply.
The refresh response publishes `GamificationCard` as a declared `ReadBlockCard`
variant, with the same card fields carried by the chat part and no extra type tag.

Complete acceptance of #1619 requires this API contract to deploy and its UI
consumer to land. Its independent merge bar is unchanged.
