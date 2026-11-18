# Home Deck — title-bar header spec (the "first bar" fix)

Status: PROPOSED — no code changes until approved.
Order: after adblock is green and reported. Small look-refinements queue after this.

## 0. What was wrong

The mockup put the avatar + name pill, back chevron, and profile dropdown in
the title bar — the first and only bar. The app shipped the OS title bar plus
a second in-app bar underneath it, with a hamburger menu nobody agreed to,
and the shield badge degraded to a gold dot that floats far from the avatar
on cards (the card's `face` grid stretches full card width, so `End/End`
corners the dot to the grid — ~30px off the avatar — plus a `Margin` hack).
This spec puts the agreed design in place and fixes the badge position.

## 1. Goal

One bar on both platforms, carrying exactly the mockup's header: back, pill
(avatar + name + chevron), dropdown on tap. No hamburger. True shield badge
(mockup shape: filled gold, dark-gold outline), hugging the avatar corner on
pill, card, and splash-adjacent surfaces. Help text updated ("gold shield",
not "gold dot").

## 2. Windows: custom title bar

MAUI/WinUI supports content in the title-bar area; that is the agreed design
and it is implementable (no new decision here — this was promised):

- `AppWindow.TitleBar.ExtendsContentIntoTitleBar = true`; caption buttons
  stay OS-drawn on the right (`ButtonBackgroundColor = Transparent`).
- Title-bar content (platform code, `Platforms/Windows/`): back `‹`, pill
  (26px avatar + name + chevron), tap → existing dropdown.
- Drag region = the whole bar minus interactive elements (back, pill, and a
  right-side reserve ~140px for caption buttons), set via
  `SetDragRectangles`, recomputed on `SizeChanged` (maximize/restore/snap).
- Physical pixels: multiply by the window's `RasterizationScale`.
- Active/inactive: dim pill to 60% when the window deactivates (match OS).
- Height: standard caption height (system metric, ~32px at 100%).
- Window title text (taskbar/Alt-Tab, always settable): launcher =
  `Home Deck`; open profile = `{Name} – Home Deck`.
- Fail-safe: if custom-chrome setup throws, keep the OS title bar AND the
  current in-app pill row (today's look minus hamburger) so the app stays
  usable. Log, never crash.

## 3. Android: native toolbar title view

No custom chrome needed. The pill becomes the `NavigationPage` title view
(avatar + name + chevron, tap → same dropdown). Back comes from the toolbar's
native Up button — no custom `‹` inside the title view (a custom one would
double it). Same dropdown, same toggle, same shield.

## 4. Hamburger removal

`OnMenu` and the `☰` button are deleted. "Open in browser" survives as a row
inside the pill dropdown (it is useful; it was only the *button* that was
unagreed). "Switch profile" becomes redundant with the dropdown itself and
goes away. Dropdown order (mockup): other profiles, "Manage profiles…",
separator, ad-block toggle row, "Open in browser".

## 5. Shield badge (pill + card) — the position fix

Shared rule: the badge is anchored to the *avatar's* corner, never to a
wider container, with a 1px overhang (right/bottom −1px equivalent).

- Pill (26px avatar): shield ~12px gold Path with dark-gold outline stroke,
  `TranslationX/Y = +1` for the 1px overhang. `IsVisible` still follows the
  open profile's `AdBlock` (already wired via `PillShield`).
- Card (64px avatar): fix the actual bug — the `face` grid gets
  `WidthRequest/HeightRequest = 64` and centers itself, so `End/End` corners
  the avatar; delete the `Margin(0,0,18,0)` hack. Shield ~16px Path, same
  overhang. `EditProfile` keeps receiving the shield view (same parameter,
  new type is still a `View`).
- Splash: no badge (mockup had none — already correct, do not add one).
- Help text (`LauncherPage.OnHelp`): "gold shield", not "gold dot".

The shield Path data (gold fill `HdGold`, 1.5px dark-gold `#7A5C14` outline)
is defined once as a shared resource/path and reused in both places.

## 6. Untouched

Launcher cards otherwise, splash, ad-block engine + hooks, `hd/1` bridge,
profile storage, ask-every-time, themes. No behavior changes beyond the bar.

## 7. Verification (user screenshots — the acceptance test)

I cannot run the Windows app or an emulator here, so "looks right" IS the
acceptance for this one (stated, not implied):

1. One bar: pill + back in the title bar (Win) / toolbar (Android).
2. Caption buttons work; drag works everywhere except pill/back/buttons;
   maximize/restore/snap keep drag working.
3. Pill tap → dropdown; all rows work; toggle syncs both ways with the
   shield.
4. Shield hugs the avatar corner on pill AND card (close-up screenshot);
   hidden when blocking is off; help text says "shield".
5. Inactive window dims the pill; taskbar text shows the profile name.
6. Both builds green (compiles — necessary, not sufficient).

## 8. Rollout

Spec approval → implement (Windows platform chrome + Android title view +
shared shield + dropdown/menu edits + help text) → both builds green →
user screenshots against §7 → small look-refinements (user's queued list).
