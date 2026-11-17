# Glimpse interface decisions

The existing Windows reader uses Segoe UI/Microsoft YaHei UI, slate text `#203044`, muted text `#617387`, teal actions `#19766F`, pale surfaces and restrained borders. Preserve these roles; new convenience controls should feel part of the same application.

The primary workflow is selection first: opening the app enters region selection, completing a valid selection starts local observation, and an attached toolbar remains beside the lens. In three seconds the user should see whether observation is running, how much was recorded, and where to stop or close it. A canceled selection leaves observation stopped and the inspector available.

The toolbar is a compact 320-DIP panel with status/counts, four capture actions, a task selector, editable prompt and one preview entry. Teal marks the active primary action; closing the observation frame is a distinct labeled action. Prefer the right of the lens, then left, below or above; clamp to the monitor work area when no outside space exists. Move it with native lens geometry, without decorative movement or animation. Content stays readable in Chinese; controls retain keyboard focus and accessible names.

Record browsing uses the existing inspector. Toolbar prompts choose comment/reply/summary/advice/custom and prepare local reviewed context there; they never submit inference automatically. Stop/close preserves recorded text. The inspector's application-close action still stops all observation. Do not add user-profile memory, automatic posting, or model-generated background actions.

Visual acceptance uses the actual WPF toolbar render and on-device native placement/clickability/capture-exclusion checks at current 96 DPI, including the negative-coordinate monitor. Mixed-DPI hardware acceptance remains open. No image assets or new visual dependency are needed for this functional change.
