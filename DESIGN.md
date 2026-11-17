# Glimpse interface decisions

The existing Windows reader uses Segoe UI/Microsoft YaHei UI, slate text `#203044`, muted text `#617387`, teal actions `#19766F`, pale surfaces and restrained borders. Preserve these roles; new convenience controls should feel part of the same application.

The primary workflow is selection first: opening the app enters region selection, completing a valid selection starts local observation, and an attached toolbar remains beside the lens. In three seconds the user should see whether observation is running, how much was recorded, and where to stop or close it. A canceled selection leaves observation stopped and the inspector available.

The sidebar is a 360-DIP panel with status/counts, capture actions, four direct task commands and custom question input. A task button sends once; it is not a selected mode waiting for another confirmation. Generation feedback and cancel remain visible while reading long results. Task names distinguish pending/completed answers. Keep the previous completed/fallback answer during a retry or failure, clearly labeled as previous, then replace it on success. Capture controls stay usable while the model runs.

Record browsing uses the existing inspector, while task answers stay in the sidebar. The owner explicitly authorized one-click sidebar generation: each click builds fresh filtered/frozen context and submits exactly that request. Optional context inspection stays inline. Opening settings is an explicit navigation action, never an automatic error recovery popup. Stop/close preserves recorded text; the inspector's application-close action still stops all observation. Do not add user-profile memory, automatic posting, or background model tasks.

An interaction-designer agent reviewed the actual sidebar render and source. Accepted changes address direct-send wording, retained previous answers, sticky cancellation/feedback and task provenance. Preserve the existing typography/palette and improve task recovery before adding visual decoration.

Visual acceptance uses the actual WPF toolbar render and on-device native placement/clickability/capture-exclusion checks at current 96 DPI, including the negative-coordinate monitor. Mixed-DPI hardware acceptance remains open. No image assets or new visual dependency are needed for this functional change.
