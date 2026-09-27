# Plex and Smart Prefetch

Connect a Plex account from **Settings → Streaming** to discover owned and shared servers. Choose a server and save it explicitly. Refresh discovery only updates the available choices; it does not change the saved server. Reconnecting, disconnecting, or editing an existing server is available from the same settings page. Tokens are masked in API responses and excluded from support packs.

Smart Prefetch is optional and disabled by default. It needs an active, healthy, writable Native Cache folder. Save Native Cache settings and restart before enabling it. The default controls select movie and TV eligibility plus a daily provider-payload budget. Expand the advanced sections to choose users, libraries, hubs, collections, schedules, and queue limits.

Verified Plex playback can request priority access to the fastest Usenet provider while background activity remains lower priority. Warming jobs use the Native Cache, share the existing connection limits, and stop when their daily budget or cache capacity is exhausted. A Plex path must map exactly to an imported DAV item. Ordinary local files are not warmed. Source or metadata failures are reported in the warming queue without interrupting playback.

The setup wizard does not require Plex credentials. Existing installations can configure Plex after setup; no imported files are rewritten. Protect `/config`, which stores account and server credentials, and back it up before upgrading.
