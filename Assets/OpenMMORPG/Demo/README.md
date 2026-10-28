# Open MMORPG Demo

A small island with a village, three enemy families, a rideable horse and a dungeon,
built to show the kit running end to end: the MMO flow (`Scenes/00Init` -> `01Home` ->
`DemoMap`), character creation with a choice of body, height, skin tone, hairstyle, hair colour and beard,
three classes with six skills each, looted armour sets, harvesting, quests, an inn and a bank, and a second
map - the Cultist Crypt (`Scenes/DemoDungeon`), reached through the crypt door in the
hills and left the same way, with a boss at the end of it.

Everything here except the scripts under `Scripts/` and `Editor/` is **generated** by the
editor tools in the separate
[open-mmorpg-demo](https://github.com/vaughanb/open-mmorpg-demo)
repository, from CC0 art libraries that are not part of the kit. Edit the generators,
not the generated assets: rebuilding the scene replaces `DemoMap.unity`.

`Scripts/` holds the components the demo is made of, in two kinds:

- **General components, with no prefix**, in the kit's own `MultiplayerARPG` namespace. These
  carry no demo content and work in any game on the kit: the foot and ladder IK, the
  day-night sky, weather and foliage wind, scenery doors, time-of-day lights, the mount
  animator, NPC patrols, the eased ladder component, skin tone and height customisation,
  the ground circles and weapon trails, the minimap camera's render throttle, and a number of fixes for kit gaps (the death cry on
  spawn, forces on remote players, knockback on standing monsters, the loading screen). The
  numbers tuned to this island's art are fields set by the builders, not constants in the code.
- **Demo components, prefixed `Demo`**, in `MultiplayerARPG.Demo`. These belong to this demo
  alone: the player controller and its auto-attack button, the home-screen camera and
  character turntable, the minimap's hand-in pin, three spell effects (Frost Nova's ice,
  Meteor, Volley), the entity setting that adds the runtime fixes, and the animation bench.

Nothing here runs a Unity `Update` per instance where there can be many instances. Per-character
add-ons ride the entity's own tick (`BaseGameEntity.onUpdate` / `onLateUpdate`); everything
else that comes in numbers is registered with the kit's `UpdateManager`, and only while it has
work: a shut door, a finished effect or a monster that cannot climb costs nothing per frame,
and a torch at rest one comparison. `Editor/` holds only the editor support for those components.

**Controls** are the ones most MMO players already know. WASD moves relative to the camera; either mouse
button dragged orbits the camera, and a right drag also turns the character; the wheel
zooms from first person out to fourteen metres. A left click on an enemy targets it, a
right click targets and attacks, Tab cycles nearby enemies, T attacks the target (or the
nearest enemy) and Space jumps. Characters start with their weapons on their back; any attack,
skill or harvest swing draws them, Z draws or puts them away by hand, and they go back after eight
quiet seconds. There is no click-to-move: NPCs, loot and
harvestables are reached by walking up and pressing the activate key. The controller is
`Scripts/DemoPlayerController` over the kit's default one; the keys are the key settings on
`Prefabs/GameInstance`, both written by `Build Player Controller`. The over-the-shoulder
shooter controller is still built by `Build Player Controller (Shooter)`.

**Each class has four fighting skills**, in `GameData/Resources/Skills`. The first is granted at
level one so the bar is never empty; the other three are bought with skill points at
character levels three, five and eight, and each can be taken to level five - which is
more than the island pays for, so the points are a choice. The warrior cleaves, bashes
with a shield (the only skill in the demo that requires one equipped), charges his target
and shouts a damage buff over his party; the ranger has an aimed shot, a slow, a
ground-targeted volley and a bleed; the mage has a bolt, a frost nova at his own feet, a
heal that falls back to himself when nobody is targeted, and a meteor. Between them they
use three of the kit's four skill classes - `Skill`, `SimpleAreaAttackSkill` and
`SimpleDashAttackSkill` - and its buffs, debuffs, ailments, knockback, cast bars,
per-weapon requirements and skill-point levelling.

**And a passive and a toggle each** (2026-09-29), the kit's other two kinds of skill. The
passives come at level two and are simply always on: **Toughness** (health and armour),
**Keen Eye** (critical chance and damage) and **Deep Reserves** (mana and its regeneration).
The toggles come at level six, stay on until pressed again, and each costs something:
**Defensive Stance** gives the warrior armour and block and slows him by 30%; **Fleet of
Foot** makes the ranger 25% faster and breaks the moment he attacks or is hit; **Arcane
Ward** gives the mage armour, stops his mana regenerating and burns ten a second, and fails
when he runs dry. Two of those terms need the demo's help, in `Scripts/ToggleBuffUpkeep`: the
kit drains a toggle's mana but never switches it off at zero, and rolls a toggle's
break-on-attack only for weapon swings, not attacking skills.

Critical hits did no damage for players until the same day - the classes had a crit chance
and a multiplier of zero - and now land for half again.

**Rage, focus and mana** (2026-10-06). Each class's MP bar holds something different, as in
World of Warcraft. The **warrior's is rage**: red, empty when he arrives or respawns, earned
at ten a landed swing (half again on a critical) and about one per 1% of his health he loses,
never regenerated, and draining at five a second six seconds after the fight. A skill paid
for in rage earns none back, so his openers are an auto-attack or a free Charge; Cleave costs
20, Shield Bash and Rallying Cry 10, Defensive Stance nothing. The **ranger's is focus**:
orange, a fixed hundred that refills at six a second in or out of a fight, so it paces the
shots - Aimed Shot 35, Crippling Shot 20, Volley 45, Hunter's Mark 15, Fleet of Foot free.
The **mage keeps mana**, the kit's own, and his attack spells cost a share of the pool so the
bar moves at every level. All three live in the kit's MP slot, so its cost checks, tooltips,
hotbar and syncing work unchanged; the rules are in `Scripts/CombatGameplayRule` and
`Scripts/ClassPowerUpkeep`, the bar's colour and wording in `Scripts/ClassPowerBar` (wired by
`Build HUD` or `Wire Class Power Bars`), and which class is which in `Wire Game Database`.

**The Hierophant has three of his own**, and they are what give the crypt's boss fight a
shape: with 645 health and one staff attack it was long and flat. At full health he casts
Unholy Nova, which lands at your feet and is the one thing in the demo that says *move*.
Below three quarters he begins calling cultists - the same entity that walks the crypt,
two at a time, capped at two alive. Below half he starts mending himself, about a fifth of
his pool, so the last stretch is a race he can win if you let him. His two long casts hold
through damage on purpose; the kit interrupts a cast on **any** hit, so left interruptible
neither would ever once complete.

**The spells carry particles.** Blue gathers in the mage's fist through an Arcane Bolt and
is thrown out of it; Frost Nova throws a ring of shards along the ground to the edge of the
area it will actually damage; Mend blooms gold on whoever it healed; Meteor draws embers in
through its long cast. The Hierophant's are the same shapes in violet. They are drawn by
`Build Skill Effects` rather than shipped, additive and unlit so they read at night, and
they hang off the `Floor` / `Body` / `RightHand` sockets every demo character carries. The
ground-targeted skills put their own particles on the area entity instead, which is what
makes a patch you can see the edge of.

**A shot in flight says which shot it is.** Four of the five missiles are the same arrow
mesh and differ only in their streak - the bow's ordinary shot is pale, an Aimed Shot is
bright, a Crippling Shot is green and a Hunter's Mark is amber - because at twenty metres
the streak is the only part you can read. The mage's bolt carries no mesh at all: it is two
additive billboards and a trail, which is what a thrown light should be.

**Every blow that lands flashes on whoever took it.** A pale spark by default - that one
reference on `GameInstance` is what a sword, an arrow, a fist and a falling rock all play -
and the spells override it with their own colour, so being hit by an Arcane Bolt looks
different from being hit by an axe. The deer and the collie got effect sockets for this;
without them a hit on wildlife was silent, which mattered because hunting is a living here.

**The in-game HUD is trimmed, not just restyled** (`Tools > Open MMORPG > Demo > Build HUD`). The kit's
gameplay canvas ships every feature it has turned on at once; the demo hides the ones it has no
content for - the RTT/server-timestamp readout, the PvP counters, vending, and the party panel that
shipped standing open - folds Crafting and Mail into the menu bar, centres the hotkey bar over the
experience bar, and repaints the placeholder-magenta headers onto the demo palette. Nothing is
deleted, only deactivated, so every kit reference stays intact. Layout follows World of Warcraft:
player frame top-left, minimap alone top-right, action bar centred at the bottom, chat bottom-left.

**The two trackers are separate, and neither is on screen when it is empty.** The kit ships party
and quest as one tabbed window in the top-left corner, which makes them mutually exclusive - you
cannot watch your objectives and your party's health at once, which is exactly what you want in a
party - and parks them on top of the player frame. Split, each goes where its subject already is:
the quest tracker under the minimap on the right, the party under the player frame on the left.
Each hides itself through a `CanvasGroup` while the kit's own "nothing here" object is showing, so
an empty tracker costs no screen. A `CanvasGroup` rather than deactivating it: `UICharacterQuests`
subscribes in `OnEnable`, so a switched-off tracker would never hear about the quest that should
bring it back.

**The quest tracker has no panel at all** - it is text on the world, the way WoW draws it. It is
not a window you opened, it is a note pinned over the scene, and the border made that worse than
untidy: the list starts flush against the tracker's top-left corner, so the frame's eight-pixel
bands sat on the first line and the quest title rendered as "hin the Camp". Clearing the backing to
zero alpha removes the fill *and* stops a border being drawn, because the skin pass skips anything
under 35% alpha - one value rather than a special case. What the panel was doing for legibility is
now an `Outline` on the text itself, which is what keeps it readable where it crosses a brazier.

**Windows move** (2026-09-24). Drag any window by its title bar and it stays where you leave it,
across sessions; it is kept on screen, comes to the front when you grab it, and a double-click on
the title puts it back. `UIWindowDrag`, on the title bar of each dialog prefab - the kit has no
window dragging of its own. The quest tracker also sits **under** every window now: it was the
last thing on the canvas, so it drew over the inventory and its quest lines took the clicks meant
for the inventory's close button.

**Every panel wears the same frame** (`Tools > Open MMORPG > Demo > Skin UI`, added 2026-09-18).
Until then the minimap was the only thing on screen with a border, and the reason turned out to be
that **1,847 `Image`s across 90 prefabs pointed at a sprite that is not in the project** - one
deleted guid, inherited when the UI was copied from the kit's template. Unity draws a *missing*
sprite reference as a flat white quad, so the entire interface was untextured rectangles tinted by
the palette, with nothing to put a border on. The pass repairs all 1,847 onto a generated surface,
then hangs the minimap's own border - the same asset, at the same size and band - round every
window, slot, button, tab and gauge: heavy on anything laid over the world, thin on anything laid
on a panel. The borders are child `Image`s rather than baked into the sprites, because a sliced
sprite is tinted as a whole and a baked border would come out panel-coloured on a panel and red on
the red close button. Run it **after** `Build HUD` and `Build Menu Stage`, which own the
colours; this owns the sprites. It is idempotent - every frame it drew is destroyed and redrawn -
and it touches nothing it did not create.

**The theme is a switch, not a decision** (2026-09-19). This is a kit demo, so the look ships
deliberately neutral - cool greys that read as chrome rather than as art direction, leaving the
logo's teal as the only accent on screen - and the whole thing moves on one line:

```csharp
internal const Theme Active = Theme.LightSlate;   // or Slate, Midnight, Bronze
```

`LightSlate` is the default and lets the chrome recede furthest; `Slate` is the same greys a step
darker. `Midnight` - near-black frames with a lavender rim, echoing the title band - was tried and
rolled back as too heavy. `Bronze` is the fantasy set this started from, kept as a worked example
of what retheming costs: six colour values. A warm-grey "stone" also shipped briefly and read as
taupe against the cyan logo.

The title bar takes the frame's **middle** step, so the border and the bar are the same metal, and
that couples legibility to the theme: white on it measures about 8:1 on bronze, 4.9:1 on slate and
**3.7:1 on light slate** - under the 4.5:1 normal-text bar, over the 3:1 large-text one. If that is
too slack in play, point `HeaderStep` at `FrameDark` and the bar becomes the frame's outer lip: one
line, same metal, far more contrast. (Midnight's bar is its own value, a hair lighter than its band,
so the title does not merge into one black slab. Everything that makes the UI *work* - that sprites exist at all, the nine-slice
frames, the spacing, the window hierarchy - is independent of them, and the minimap moves with the
rest because they share one frame asset.

Switching required fixing a trap first. **Colour matching works exactly once**: a repaint that only
knows the kit's placeholder finds nothing to do on its second run, and after a retheme the panels
are not white, they are the *previous* theme's. `DemoPalette` therefore lists every value the demo
has ever painted - including retired ones - and the repaint passes move any of them onto the active
one. That is what makes `Active` a switch you can flip twice, and it is why a theme's values have to
outlive the theme: drop them and a project skinned during that window can never be re-themed.

The same pass opens the layouts up enough for those borders to read. The kit packs its rows edge to
edge, which is survivable while every row is a flat quad of the same colour and stops being
survivable once each one has a border: two lines of metal touch with nothing between them. Rows are
**moved, never shrunk** - the text inside a field is stretch-anchored to it, so taking six pixels
off a 30px field leaves an 11px text area that cannot hold a 14pt line - which means a panel that
is already full has to get bigger, and the pass stretches it by the shortfall, capped at 40% of its
height.

**The home menu is a real set, not a backdrop image** (built 2026-09-16). `01Home` shipped
as a flat orange camera clear colour, one light, an invisible `Plane` and a character standing
in a void. It is now **the top of a road** built from Quaternius village and nature pieces: flat
cobbles where the character stands, then a brow and a hillside running down towards the
valley image on a quad behind it, with grass verges, lit braziers stepping down beside the road, a cart
on the verge and trees standing on the slope. Every prop is seated on the hill by depth alone, so
nothing has to be positioned by hand and nothing can end up in mid-air.

**The camera is two framings, not one.** The scene is saved wide - up at 3.4m, pitched 14 degrees
down - which is what it takes for the road to read as a road. One lens could not do both jobs: from
a portrait framing the whole descent compresses into about 9% of the frame's height.

`DemoMenuCamera` sits on the camera and travels between the two over 0.85s, easing in and out, so
logging in flies down the hill to the character rather than cutting to them. The character screens
ask for the close framing and give it back; the rig counts how many are holding it, so stepping
between select and create does not send the camera away. Because the rig is on the camera and not
on a screen, the move back still finishes after the screen that asked for it has gone. Every home screen shares one camera, so the server list, login, register, character
list and character create all get the set. The **Open MMORPG logo** appears on the four screens
before the character ones - server list, login, register, channel list - by living inside each of
those screens' own containers rather than on a canvas, sitting on a soft dark scrim that is
generated rather than drawn. It stays off the character screens, where the character is what you
are meant to be looking at. Those two screens instead get
`DemoCharacterPreviewControl`: drag anywhere off the panels to turn the character, scroll to
move the camera in and out. Both reset when you leave the screen, since the camera is shared
with the login and server screens.

The backdrop is deliberately **cropped to the image's upper band**. The art has its own
warrior, ranger and mage standing at the bottom right, and with a real character on the terrace
in front of them the frame had two sets of adventurers competing; the crop keeps the sunset,
mountains and walled city and leaves the pictured party out of shot.

**Four quests, four quest givers** (three added 2026-09-16). They ladder with the island's
enemies: the town guard on his rounds wants five wolves thinned off the fence line (level one,
the first thing a new character can do), Elder Rowan wants eight bandits off the headland,
Hilde the innkeeper wants four cuts of venison for her pot - the one fetch quest, and the
hunting alternative for a character who would rather not fight a camp - and the watchtower
guard wants the Hierophant dealt with at the bottom of the crypt, gated behind Rowan's quest
and level six. Rewards scale from Rowan's existing 400exp/250g; the boss pays in coin because
coin suits all three classes.

**A fifth, the errand** (2026-09-26): Hilde asks you to find out why Bram the smith has missed his
supper three nights running. Talk to him at the forge, then bring his answer back to her. It is
the demo's only Talk to NPC task, and it sends a new character past the forge. While the quest
is open, Bram opens with his answer instead of his greeting, and "While I am here..." leads on
to his usual menu. The kit knows the NPC by his prefab's network asset id, so the target has to
stand on a prefab of his own: Marek and Oswin share one and would both answer.

**Where to hand a quest in** (2026-09-24). Each quest giver carries a marker, over their head
and on the minimap: a gold `!` for a quest on offer, a silver `?` for one under way, and a
gold `?` once its tasks are done. A gold `?` whose NPC is off the minimap stays pinned to the
minimap's edge, pointing the way back. Hilde and the watchtower guard keep their quests behind
a greeting, so their menus change line with the quest - "I have your venison" appears once you
are carrying it, which is where the Complete button is. Before this the markers all drew as
the same plain disc and Hilde's only line read "Is the pot empty again?", and a player back
from the hunt with four cuts could not find the hand-in.

**You can have a wolf of your own** (added 2026-09-22). Marek sells a **Pup's Collar** for
three hundred gold - the one thing on his board worth saving for - and using it calls a
**wolf pup** that follows you and fights what you fight. Using it again sends it away, and
only one can be out at a time. It is the same animal as the wolves on the fence line, two
thirds the size, pitched under them so it helps a new character rather than fighting for
them. It gives nothing for killing it, which matters when the player is the one who
summoned it.

**Not every hit is the same kind of hit** (added 2026-09-22). Until now every blow on the
island was one undifferentiated thing: the mage's frost nova and meteor were frost and fire
in name only, and armour resisted damage in general. There are now three **damage
elements** - physical, fire and frost - and the mage is the only one who deals the last two.
They leave marks: Meteor sets its target **Burning**, Frost Nova leaves them **Chilled** and
slowed by a third, and the ranger's Hunter's Mark now causes **Bleeding** by name rather
than by an unlabelled timer. Each is a `StatusEffect`, which is a named buff that gear can
resist.

And the three armour sets the island drops are **sets** at last. Two pieces, three and four
each add something, and they stack: the knight's harness is health and physical armour, the
ranger's kit is speed and a little more critical chance, and the wizard's vestments are the
only thing on the island that resists fire and frost - which is the only reason a mage would
walk into the crypt in cloth. Sixteen pieces across the three, all of them things an enemy
was already wearing when you killed it.

**Who can wear what** follows World of Warcraft's armour weights (2026-10-06): a class wears its
own weight and anything lighter. Cloth - the peasant clothes and the wizard's vestments - is
anyone's; leather (the ranger's kit and the bandits' black copy) is the ranger's and the
warrior's; plate (the knight's harness) is the warrior's alone. Weapons go by class: swords and
the shield to the warrior, bows to the ranger, staves to the mage; the woodcutter's axe and the
miner's pick are tools anyone can carry. The table is `ClassItems` in `Wire Game Database`, on
each item's kit `requirement.availableClasses`, which the kit checks as you equip and shows in
the tooltip.

**Bows need arrows, gear has sockets, and there is a way home** (added 2026-09-22). Three
item kinds the demo never made. **Arrows** are an `AmmoItem`, and the requirement sits on
the Bow weapon type, so both bows want them and an empty quiver means no shot at all - a
ranger starts with a hundred, Marek sells them at a gold, and twenty come off one length of
timber at any craft window. They carry a point or two of damage of their own. The **Scroll
of Return** sends a player back to the shrine they bound themselves to, which is what makes
binding at the crypt worth doing. And the best weapon and body armour of each class's line
now have **sockets** - two in a weapon, one in a chest piece - for the three gems the pedlar
sells: a garnet for health, a sapphire for magic, a citrine for attack speed.

**A guild has something to be** (added 2026-09-22). The guild system was never switched
off - fifty members, a role table, a fifty-level experience tree and a thousand-gold
founding fee have been in `SocialSystemSetting` from the start, and the guild windows are
in the HUD. It was simply **empty**: a guild levelled, collected a skill point a level, and
had nothing to buy. It now has three passive skills to spend them on - **Fellowship** (four
more members a rank), **Shared Lessons** (3% more experience for every member a rank) and
**Common Purse** (3% more gold) - and six crests to fly. Fenwick keeps the guild chest
beside the player strongbox, eighty slots to the personal forty, reachable by anyone
wearing the badge.

A guild earns its experience from the share of a kill its members choose to give it, up to
the setting's 20% cap, so the loop needs nothing seeded: found one, set a share, go hunting.

**Four places in the village craft things** (switched back on 2026-09-22). The cookfire on
the green, the anvil in the smithy, the potion stall in the market row and a fletcher's
bench built beside it are `WorkbenchEntity` buildings carrying eleven of the island's
fourteen recipes between them - so a stew is cooked at a fire and a sword is made at a
forge, rather than everything being made anywhere from the Craft button. Three of the four
are furniture the village already had.

They were disabled for four days because they killed the map server on startup. A scene
building is saved under `ChannelId_MapId_SceneObjectId`, the scene object id was generated
rather than chosen, and rebuilding the village renumbered it - so the saved row matched
nothing on the next start and the server died loading it back. Each station now pins its
own id by name.

**There is one thing you build yourself** (added 2026-09-22). Marek sells a **Campfire
Kit**, and four timber, two stone and a hide will make one at the craft window. Take it out
of your pack and it goes into the kit's build controls - aim, turn, set it down on open
ground, one per player. It is not a prop: a campfire is a `CampFireEntity`, which is a
`StorageEntity`, which is a `BuildingEntity`, so it is a container you open like the bank's
strongbox, and what it holds it works on. Feed it timber and it lights; put the venison you
shot in beside the fuel and it comes back as stew. Break it down and you get some of the
wood back.

That one item is the demo's whole showing of five kit systems it had nothing for -
`BuildingItem`, `BuildingEntity`, the build placement controls, `StorageEntity` and
item conversion. It is also the only place the island turns one item into another over
time, rather than at a craft window.

**Gear wears out, and Bram the smith puts it right** (added 2026-09-22). Every weapon,
shield and piece of armour now has a durability, which it never had: a weapon loses half a
point every blow it lands, armour a tenth of a point every blow its wearer takes, and the
kit steps what a worn piece is *worth* down long before it breaks - full value above half
durability, then three quarters, a half, a quarter, and nothing at all under five percent.
Nothing is ever destroyed; a broken sword is a useless sword, not a lost one.

Bram stands at the anvil in House_2, which has been a fully furnished smithy with nobody in
it since the village was built. He does the three things the kit can do to a piece of
equipment and the demo had never shown: **mend** it, **refine** it on the wheel - three
steps, rising cost, falling odds, paid for in gold and the island's own stone - or **break
it down** for the material it was made of. Metal gear returns stone, bows and staves timber,
cloth and hide leather; a smith pays in material where Marek pays in coin, so the two are
worth reaching for at different times. What each job costs comes from one of three
`ItemRefine` assets - plain, fine and masterwork - picked by what the item is worth.

**There is a shrine outside the village, and dying means something different once you
have used it** (added 2026-09-19). It stands twenty-one metres north of the green, out
through the gap in the fence past the north houses: a stone arch over a ring of runes, on
a paved round between two braziers that never go out. Activate it and it offers to bind
your spirit; confirm, and you wake there afterwards instead of wherever the character was
created. That is the kit's own `SaveRespawnPoint` dialog and nothing else - a shrine is an
`NpcEntity` with one dialog, no model and no code, which is all `BaseGameEntity` needs.
Before it the demo never wrote a respawn point at all, so every character kept the one it
was born with.

It began as an assembly of library pieces, the way the crypt's altar still is, and is now
the user's own `Shrine_Wayside` model - a carved stele over a basin on a stepped plinth,
with its own brass fire sockets. It **seats itself**: it settles onto the ground under it,
bedding into the slope, and the painted grass is cleared from under the flagstones so
nothing grows through them.

**The village is a safe zone, and there are five more things to carry** (added
2026-09-22). A `SafeArea` covers the green out to twenty-two metres, which is the kit's
"town is town" rule and the demo had none: inside it nothing can be damaged - a wolf that
follows you in cannot be killed until one of you leaves - monsters that wander in turn
round and walk home, duels are refused, and a campfire cannot be built. The last one
surprises people; it is the kit's rule, not a broken item.

**Everywhere outside it is open PvP** (2026-09-29): the island's map info is set to the
kit's `Pvp` mode, so any player who is not in your party can be attacked, and can attack
you. The safe zone is what keeps the village out of it, with no rule of the demo's own -
the kit makes anyone standing in one unhittable and stops anyone standing in one from
hitting out. The shrine sits just inside the edge (21 m from the middle), so a character
bound there gets up somewhere safe; Oswin's homestead plot is well outside, and the crypt
keeps the kit's default of no PvP. With nothing targeted, the attack key, the Attack button
and attacking skills take the nearest enemy - which on a PvP map can be a player.

With somewhere safe to go, the pedlar's board finishes the item catalogue. A **Town
Charm** (45g) always takes you to the village, whatever you are bound to - which is the
real difference between it and the Scroll of Return, the moment a player binds somewhere
else. A **Passage Stone** (150g) opens onto the crypt's landing from anywhere on the
island: the one cross-map warp a player carries, and the same handover the crypt door
does. A **Tome of Insight** (400g) is three hundred experience now and half again as much
for the next ten minutes. A **Sealed Cache** (250g) pays out three pulls from a weighted
table - potions, timber, stone, leather, arrows, and sometimes a gem. A **Scroll of
Mending** (60g) casts the mage's heal in anybody's hands, which is what a warrior with no
healing at all is for. There is a **Minor Mana Potion** on the board too, which there
never was - the island had no mana-bearing anything but spiced wine at six a cup. Only the mage
can drink it (since 2026-10-06): the warrior's and ranger's MP is rage and focus, and forty
of either for twenty-five gold would undo both.

And the watchtower guard will now put you on the crypt's doorstep for twenty-five gold,
once you have taken his errand. That is the demo's only `NpcDialogType.Warp`, and the
last of the kit's eleven dialog types nothing used.

**Kills leave a lootable body.** `monsterDeadDropItemMode` is `CorpseLooting`: the whole kill's
loot goes into one `CorpseEntity` that the player activates like any other interactable. It was
`DropOnGround` until 2026-09-16, which spawns an `ItemDropEntity` per item - and that entity
draws **the item's own `dropModel`**, which not one of the demo's items has, so every drop from
every enemy landed invisible. Nothing on the island had ever actually been lootable.

**The chests open** (2026-09-30). The seven treasure chests - three in the bank vault, one in
the bandit camp, three in the crypt - were scenery; each is now a `TreasureChestEntity`, the kit's
corpse entity standing still, so it takes the same click, the same loot dialog and the same
server-side distance check a body does. The lid swings on the Quaternius pack's own open and
close clips. A chest rolls two or three kinds of thing from `ChestLoot` (provisions, arrows,
materials, the odd scroll or garnet) and fills itself again five minutes after it is emptied;
the two legendary chests roll three or four from `LegendaryChestLoot` (the Sealed Cache, the
gems, the tome, the passage stone) and take a quarter of an hour. An emptied chest stands open
so it reads as looted from across the room. The lid has a voice: `ChestOpen` as it goes up and
`ChestClose` as it comes down, and the village doors got theirs the same day - `DoorOpen` as the
leaf starts to swing and `DoorClose` when it meets the frame, not when it leaves it. All four are
clip families under Demo/Audio that `Wire Audio` puts onto the chest prefabs, the six doors in the
scene and the homestead door. `Build Treasure Chests` builds the prefabs and tables and swaps the
chest props in both scenes for entities; the scene regenerates call the same swap at their end.

**Every player sees the same doors and chests** (2026-10-04). A village door is the server's: a
press asks the server through the player's own `PlayerCharacterInteractionComponent` (which checks
the player is in reach), the server opens it and shuts it again five seconds later, and every
client in range swings its leaf when the state arrives - so a door one player opened is open, and
solid where its leaf stands, for everyone. The state sits on a `DoorSync` child of each doorway with
its own network identity (`SceneryDoorSync`, scene id `Door@<path>`, written by `Network Village
Doors`), not on the doorway, because the network hides a scene object out of range and would take the
door with it. A chest's lid stands open while it is empty *or while anyone has it open to loot*:
each client tells the server when it opens and closes the loot window, and the server keeps the
looters, dropping any who walk off, die or disconnect. Both need `Build Map Server` after a change.

**Each enemy family drops its own trophy** - a bandit insignia, a marauder's seal, a cultist's
sigil - plus its own armour set. Until 2026-09-16 every human dropped the *bandit* insignia,
cultists and the crypt's master included. The animals are quarry and carry neither a token nor
a potion: a wolf leaves a pelt and a fang, a deer venison and a hide.

**Wolves are the island's starter enemy** (added 2026-09-16). Everything else that fights
is a person, and the weakest of those - a level one bandit - has 55 health and an axe, which
is not what a character should meet on its first walk out of the village. The wolf is pitched
under a bandit at every level (44 health at level one against the bandit's 55, no weapon) and
is the one enemy that is `Aggressive` rather than `Normal`, so it comes to the player instead of
waiting to be struck. Its 44 (and the deer's) is set so that no class's level-one opening cast -
the warrior's Cleave tops out at 40, the mage's Arcane Bolt at 36 - kills it outright without a
critical.
Four small packs ring the green at 41m - east, south-east, south and west - so the first
fight is the same short walk whichever way you leave; north is left out because the shore is
there. They drop a pelt and a fang, and no gold, having no pockets.

The wolf is the user's own model, retopologised from 1.9M triangles to 6k and rigged onto the
collie's skeleton in Blender - the same 65 bones under the same names - so all twelve of the
dog's clips drive it directly and it needed no new animation.

The warrior's skills multiply his weapon's damage, so a better sword is a better Cleave;
the mage's carry their own numbers, so a staff is only what lets him cast. The skill icons
were generated with Ludo AI (see `CREDITS.md`), and the three ground-targeted skills each spawn
their own marker disc sized to the area they actually cover.

**The sea is swimmable, on the surface only.** Walk in off any beach: the character wades
until the bottom drops away, then swims at eight tenths of run speed, held at the surface.
There is no diving. The water is a trigger volume on the Water layer under the sea plane
(`Rebuild Sea` puts it there), the player entities and the horse have the kit's
autoSwimToSurface on, and `Scripts/SurfaceSwimmer` lifts the model to the surface
while swimming, because the kit holds the capsule deep enough for treading water and the
swim clips lie flat.

**Boot prints and splashes.** Players leave prints in the beach sand and throw up splashes, ripples
and a wake in the sea. `Scripts/FootstepEffects` on the player entity watches the feet's sole
contacts after the IK has run (so the prints land under the real feet, and another player's show
the same from their synced pose); `Scripts/FootstepEffectsHub` draws everything, in seven particle
systems shared by the whole scene. Prints fade out where the sand layer does, are darker and crisper
on wet sand and in the surf, and a boot that has been in the sea leaves wet ones for a few steps.
Built by `Tools > Open MMORPG > Demo > Build Footstep Effects`; the how and the traps are in the builder's
README under "Footsteps".

**Audio** lives in `Audio/` and is wired by name: a family is a prefix plus a number
(`Footstep1.wav`, `SwordSwing3.wav`, `WomanHit2.wav`), and `Tools > Open MMORPG > Demo > Wire
Audio` hooks every family the kit has a slot for and logs the ones still empty. Footsteps
go on every character through the kit's footstep component - the players' play on each real foot
landing (`FootstepEffects` calls the component when a foot comes down), everyone else's on the
kit's timer; sword and axe swings on the
attack animations; bow shots and staff casts on the weapon item, at the launch; hurt
grunts through `Scripts/CharacterHurtSoundComponent` on the synced HP (the kit has no
client-side hit event); the island's nature and shore loops are built with the sea, the
shore fading with height through `Scripts/AmbientSoundLoop`. The skills reuse the families
they belong to - Cleave takes a `SwordSwing`, the mage's bolt a `SpellCast` - and name
three of their own: `ShieldBash` and `SkillImpact`, which fall back to `PunchSwing` and
`SpellCast` until clips exist for them, and `Shout` for the warrior's Rallying Cry, which
is silent until one does. `Wire Audio` lists all three in its report.

Art credits are in `CREDITS.md`.
