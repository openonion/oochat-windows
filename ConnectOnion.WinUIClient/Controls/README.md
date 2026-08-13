# Controls/

Reusable `UserControl`s, grouped by the surface they belong to.

**All files here keep the flat `ConnectOnion.WinUIClient.Controls` namespace regardless of
subfolder.** This is deliberate. A control's CLR namespace is what every consuming XAML file
names in its `xmlns:controls="using:ConnectOnion.WinUIClient.Controls"` declaration (42 files do)
and what `x:Class` in each `.xaml` must match. Making the namespace follow the folder would mean
editing every one of those for zero behavioural gain, and a mismatch surfaces only at XAML-compile
time. The folders are physical organisation; the namespace is the contract.

A `.xaml` and its `.xaml.cs` must always live in the same folder.

| Folder | Contains |
|---|---|
| `Chat/` | The composer (text, attachments, speech), tool-activity cards, interactive cards, offline bar |
| `Agents/` | Agent identity UI: add form, avatar, and share dialog |
| `Settings/` | Settings overlay and panes, identity dialogs, usage heatmap, shortcut editing |
| `Shell/` | Window chrome: sidebar, global search, in-app notifications, about overlay |
| `Primitives/` | Feature-free text, animation, resize, thinking, and modal-accessibility building blocks |

`Primitives/` is the one with a rule attached: nothing in it may reference a view model or a
feature-specific model. If a primitive needs that, it belongs in one of the other folders.
