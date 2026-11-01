# Gate r9i captures

These are the unmodified tool calls from the production gate attempts named by each
file's case ID. Preview rejection captures retain the setup turn, which failed before
the correction could run. Other account data and answers are omitted.

The write replays inject each recorded call at `IAiIntentService`, use the real
operation executor and previewer, and require one approval preview without a repair
round or command dispatch. The coaching capture records the duplicated non-saving
suggestion call and its incorrect `merged_into_preview` response.
