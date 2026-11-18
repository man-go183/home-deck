# Home Deck — launcher refinements spec (batch before the single build)

Status: PROPOSED — no code changes until approved.
Order: adblock report → this batch → ONE build round (android, windows) → screenshots → report.

Ground truth: `scratch/mockup-backup-20261117/index.html` (items below cite it).
Deviations from it are bugs, not choices.

## R0. Shield path correction (fold into header files)

The header push used an invented 12×14 shield path. The mockup's real
assets (backup lines 330–341) are:

- Shield: `M12 3l7 2.8v5.1c0 4.6-3 7.9-7 9.6-4-1.7-7-5-7-9.6V5.8z`
  (24×24 box), gold fill `HdGold`, 1.5px `HdGoldDeep` outline.
- Off-state slash: `M5 5l14 14` (second Path, visible only when off, dim fill).

Replace the invented path in `BrowserPage.xaml` (`PillShield`) and
`LauncherPage.xaml.cs` with this one (`Stretch=Uniform`, sized 12–16px).
This supersedes spec §5 of `titlebar-header.md` on the path data only.

## R1. Person-glyph avatars, no letters

Default avatar everywhere (card, pill, splash, switcher): profile-color
disc + person glyph in near-white, from the mockup (backup line 331):
circle `(12,8,r=4.2)` + shoulders `M3.5 21c.8-4.4 4.3-7 8.5-7s7.7 2.6 8.5 7z`
(24×24). MAUI: `GeometryGroup` of `EllipseGeometry` + `PathGeometry`
inside a `Path` (XAML) / same in code-behind (card). Delete the
`Name[..1]` glyph everywhere.

## R2. Card shield: corner of the CARD, clickable, true shape

- Position: bottom-right corner of the card border (mockup item 14), NOT
  the avatar corner. Overhang 1px like the pill.
- Behavior: tap toggles that profile's `AdBlock` (with save + pill/splash
  sync where visible) WITHOUT opening the profile. Hit area ≥ 24×24.
- States: gold filled shield + outline = on; dim fill + visible slash = off.
- The header's pill shield (avatar corner, non-interactive) is unchanged.

## R3. Inline card editing (replaces all native prompts)

Each card flips front ↔ edit face in place (same pattern as the mockup's
squash-flip; on MAUI a simple crossfade/scale is acceptable — no 3D).
Faces share one fixed height so the card never resizes (see R6).

- Front face: avatar (glyph/gradient/upload), name, page-id, card-corner
  shield (R2). Tap card = open. Pen badge on avatar's lower-right = flip
  to edit. Delete the "Edit" button (its menu had no avatar editing).
- Edit face: avatar preview + pen (opens the avatar popup), name `Entry`,
  link `Entry`, amber warning label shown only when the link is not a
  Perchance page (warning never blocks saving), Save / Delete buttons.
- Avatar popup (small overlay like the header dropdown): Upload image
  (`MediaPicker`, stored under `AppDataDirectory/avatars/`, shown cropped
  round via `EllipseGeometry` clip), 6 preset dots (4 exact linears + 2
  conic→linear approximations with identical stops, documented in code),
  Default row (person glyph on profile color). Tapping a preset clears any
  upload.
- Conic note: MAUI has no conic brush. The two conic presets become
  135° linears over the same stops:
  `(#f6a35c,#e14b32,#7c3fa3)` and `(#3ec6e0,#7bc47f,#f6a35c)`.
- Model: `Profile.AvatarKind` (`default`|`gradient`|`file`) +
  `Profile.AvatarValue` (gradient index or filename). Missing = `default`
  (existing installs keep color + glyph). `ProfileStore` persists both;
  unknown values fall back to `default`, never crash.

## R4. Real Help card (replaces the `DisplayAlert`)

Tapping `?` opens an in-app overlay card (same Border pattern as the
header dropdown), scrollable, with the mockup's exact content
(backup lines 296–323): title "Using Home Deck", the 6 steps verbatim
(step 6 says **shield**, step 2 says pen/avatar/preset editing, step 4 the
header switch), the collapsible "Advanced: device features a page can
request" with all 13 items + the permission note, "Got it" + tap-outside
/ back-button to close.

## R5. Inline new-profile form (replaces the two prompts)

The add-card expands into a form card: name `Entry`, link `Entry` with the
mockup hint ("Your Perchance page link — e.g. perchance.org/home-deck"),
"Block ads & trackers" checked by default (resets to checked after add),
Add / Cancel. Same amber non-Perchance warning as R3. `https://` prepended
when missing.

## R6. Card size

`WidthRequest = 208`. Face area fixed height 274 (front and edit faces
identical, so flipping never resizes the card). Add-card matches
(208 wide, 274 min-height, centered `+`).

## R7. Ask row = one tight unit

Checkbox + "Ask every time" adjacent (`Spacing = 8`), whole row toggles,
vertically centered under the cards. Keep the native `CheckBox` with gold
color (a fully custom box is cut — native is fine at this size, and the
mockup's custom box was a web affordance). Verify adjacency by screenshot:
in the current build the box and label render ~200px apart, which is the
bug being fixed (likely a layout-options issue, not spacing).

## R8. Footer version

`v0.1` (not `v0.1.0`).

## R9. Card surface

Keep the current border + fill; strengthen the stroke slightly to echo the
mockup's glass edge. No hover states in v1 (touch-first targets; Windows
hover polish is cut, not forgotten).

## Verification (screenshots — the acceptance)

1. Launcher: glyph avatars, shield on card corners (close-up), 208px cards,
   tight ask row, `v0.1` footer.
2. Shield tap toggles without navigating (two screenshots, on + slashed off).
3. Card flip: front and edit faces, same card height both ways.
4. Avatar popup: 6 dots + upload + default; pick a gradient → card + pill
   update; upload → round-cropped image.
5. Non-Perchance link → amber warning (edit + new-profile forms).
6. Help card open with steps + expanded Advanced list.
7. New-profile form with hint + checked blocker box.
8. Both builds green (necessary, not sufficient).

## Rollout

Spec approval → implement R0–R9 (launcher files + model/store only; no
engine/header/bridge changes) → single build round (android-apk first,
windows-exe second) → screenshots vs §verification → one report.
