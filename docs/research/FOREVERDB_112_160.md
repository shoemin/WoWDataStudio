# 1.12 → 1.60 changes, as catalogued by ForeverDB

A pick-and-choose log of what WoW Forever (client 1.60.1.70450) changes compared with Classic. It is built from
[foreverdb.net](https://foreverdb.net), mainly [Forever for Classic veterans](https://foreverdb.net/veterans) and the pages
it links to. It also adds our own checks against the 1.60 beta client files where they help: addon names, API and tables.

* **Source and date.** ForeverDB pages read on 2026-10-20. ForeverDB's data is its own extraction from the beta client
  (build 1.60.1.70450, extracted 2026-10-19), plus Blizzard's posts. The text here is our summary, not a copy. Each
  section links the page it comes from.
* **Baseline.** ForeverDB compares Forever with **Classic Era (1.15.x)**, not 1.12.1. Era is 1.12 content with later
  fixes, so a small share of the "changes" below were already different between 1.12.1 and Era. Before porting
  something, confirm it against our 1.12 data: Data Studio's *Compare across versions* or the update catalog.
* **Our own client-file diff** is [DIFF_112_160.md](DIFF_112_160.md): engine, graphics, UI framework, file inventory
  and table schemas. This file covers gameplay and features.
* **Class-by-class details** (new abilities, talent reworks, changed spells) are in
  [FOREVERDB_CLASS_CHANGES.md](FOREVERDB_CLASS_CHANGES.md).

**Tags on each item: what porting it to our 1.12 would take.**

| Tag | Meaning |
|---|---|
| **[data]** | Database rows only (spells, items, quests, numbers). A 1.12 server (VMaNGOS) plus Data Studio can apply it; the 1.12 client already knows how to show it. |
| **[assets]** | New art: models, textures, maps or sounds. It needs conversion (the map converter, texture down-conversion) plus data. |
| **[client]** | Needs new 1.12 client code or UI (a mechanic or screen 1.12 does not have). Usually server work too. |
| **[server]** | Server logic VMaNGOS does not have (rules, systems), but no client change. |

---

## 1. Features you flagged: Legacy System, Cooldown Manager, gamepad

### 1.1 Legacy System: account-wide progression **[client] [server] [data]**

From [ForeverDB: Legacy calculator](https://foreverdb.net/legacy) and
[The Legacy System explained](https://foreverdb.net/article/legacy-system-explained):

* **Points.** 81 *Legacy Challenges* across the account each give 1 Legacy Point:
  * Classes 27: each class reaching 25, 45 and 60.
  * Tradeskills 18: each crafting profession reaching 150, 225 and 300.
  * Dungeons 3: lists of dungeons per tier.
  * Raids 3: Hyjal Summit, the Barrow Deeps, Onyxia.
  * PvP 12: honor ranks 3, 7, 10, 13 and 14, exalted with each battleground faction, Field of Honor weeks.
  * Adventure 18: beta participation, the Valthalak questline, Explorer, and more.
* **Perks.** Each character spends up to **16 points** on **21 perks (69 ranks)** in three trees. Higher perks need
  5 or 10 points in the tree and a prerequisite perk.
  * **Professions:**
    * Master Chef: chance of extra cooking results.
    * Working Overtime: skill-up chance +4–20%.
    * Bartering: vendor prices −5/10%.
    * Dedicated Study.
    * Bountiful Harvest: more *Scarce* materials.
    * Performance Bonus: crate turn-ins.
    * Luremaster: extra fish.
  * **Adventure:**
    * High Alert: stealth detection.
    * Well Rested: rested XP rate and cap +4–20%.
    * Talented: talent points start at level 9 down to 5.
    * Thrill of Adventure: health and mana on a killing blow.
    * Field Guide: camp feature cooldown.
    * Frequent Flier: flight paths −50% cost, +20% speed.
    * Field Medicine: shorter Recently Bandaged.
  * **Resourcefulness:**
    * For Great Honor: +2–10% honor.
    * Gourmand: food buffs last longer.
    * Permanence: group buffs and camp buffs last longer.
    * The Quick and the Dead: faster while dead, free spells after a resurrect.
    * Reagent Economy: no vendor reagents for class abilities.
    * Reinforce: less durability loss.
    * Diplomat: +2–10% reputation.
* **Reward track.** Rewards at 15, 25, 40 and 55 points: an air-rifle toy, a Spectral Bear Cub pet, a tabard and the
  Spectral Bear mount. They are claimed from an innkeeper in Ratchet.

**In the 1.60 client** (our checks, `scratchpad/casc_probe/p23`/`p24`):

* The UI is two addons that load only in Forever (`AllowLoadGameType: camelot`):
  * `Blizzard_LegacySystem`: 19 files, 122 KB, load-on-demand. It depends on Blizzard_SharedTalentUI,
    Blizzard_AchievementUI and Blizzard_ProfessionsTemplates.
  * `Blizzard_LegacyChallengeTracker`: 1 file. It saves `LegacyChallengesUnviewed` per character.
* **It is built on Retail's systems.** The perk trees run on **C_Traits**, Retail's talent-tree engine:
  GetConfigIDByTreeID, ResetTree, GetTraitCurrencyForAchievement and others. The tables are TraitTree, TraitNode,
  TraitDefinition, TraitCost, TraitCurrency and TraitCond. Points come from **achievements**
  (TraitCurrencyForAchievement). The reward track uses **C_MajorFactions** renown levels.
* **Port assessment.** 1.12 has none of these: no traits engine, no achievements, no renown. A port means a
  1.12-native version:
  * a server-side account table of challenges and points;
  * perk effects as server auras or spells **[server] [data]**;
  * a small custom UI frame in the 1.12 client **[client]**.

  Most perk effects can be done with 1.12-era spell auras (reputation %, rested rate, vendor price, durability,
  skill-up chance). *Talented* (early talent points) and *Frequent Flier* need server rules. Running the Retail
  addon as-is is not realistic: it needs C_Traits, C_MajorFactions and the achievement API.

### 1.2 Cooldown Manager **[client]**

From [ForeverDB: September 24 beta notes](https://foreverdb.net/article/beta-development-notes-september-24) and the
veterans page:

* A built-in display that collects key cooldowns, procs and buffs from your action bar and buff bar, and shows them
  wherever you place them, fully customisable.
* It is off by default. You turn it on in Options, under Gameplay Enhancements.
* During the beta it was first testable for Druid, Mage, Priest, Warrior and Warlock. Pet cooldowns and many other
  effects can't be tracked yet; it is still in development.

**In the 1.60 client:**

* `Blizzard_CooldownViewer`: 26 files, about 350 KB. It loads in standard WoW and in Forever.
* It uses **37 C_ functions**:
  * C_CooldownViewer: GetCooldownViewerCooldownInfo, GetGroupBuffItems, IsCooldownViewerAvailable.
  * C_UnitAuras: GetUnitAuras, GetUnitAuraBySpellID, SetHiddenGroupBuffs.
  * C_Spell: IsSpellUsable, SpellHasRange.
  * C_SpellActivationOverlay.
  * Edit Mode for placement.
* Table `CooldownViewerItemSpell`: which item and spell cooldowns are tracked.
* **Port assessment.** Feasible in 1.12 as an addon-style frame. The 1.12 Lua API already has GetSpellCooldown,
  GetActionCooldown, UnitBuff and the spellbook functions. A 1.12 version means:
  * rewriting it against those calls;
  * adding a placement or move mode, since 1.12 has no Edit Mode;
  * optionally a client-side list of which spells to track (the CooldownViewerItemSpell idea).

  No engine work is needed. It is one of the cheapest of the new features to bring over, because it is pure UI on
  data 1.12 already has.

### 1.3 Gamepad / controller support **[client]**

From the [BlizzCon recap](https://foreverdb.net/article/blizzcon-2026-whats-next-for-forever) and the September 24 notes:

* Official gamepad support, labelled "Gamepad (Alpha)" in the beta settings.
* **Action bar.** Class flyouts on the bar: Druid forms, Hunter and Warlock pet control and pet spells, Paladin
  auras, Warrior stances. The pet bar no longer replaces the bottom gamepad bar.
* **Options:**
  * Use Compact Action Bar: shows only the active bar.
  * Fixed Party Targeting: in a party, each d-pad button is a fixed party member.
  * Swapping the sides of the targeting modifiers.
* **Integration.** Edit Mode layouts work with the gamepad UI. The map, quest log, compact raid frames and role
  poll have gamepad bindings and navigation.

**In the 1.60 client:**

* Addons:
  * `Blizzard_Gamepad`: 15 files, 115 KB, Forever only.
  * Its dependencies `Blizzard_GamepadSmartNavigation`, `Blizzard_GamepadSharedUtility` (input binding stacks,
    prompts, handlers) and `Blizzard_GamepadSoftCursor`.
* XML gets `OnGamePadButtonDown` and `OnGamePadStick` handlers, also used by the color picker and quick-keybind.
* APIs: C_GamePad (GetActiveDeviceID, GetDeviceMappedState, StickIndexToConfigName and others), C_GamepadUI (action
  storage slots per stance), C_KeyBindings binding contexts, temporary CVars.
* **The real work is in the engine.** The exe reads the controller (XInput or GameInput), maps sticks and buttons,
  and drives the soft cursor and free-look camera. The Lua is the presentation layer.
* **Port assessment.** The biggest engine job of the three:
  * **Core:** controller input in the 1.12 client (XInput polling, button → binding mapping, stick → camera and
    movement, a soft cursor). Our decomp hybrid can add that in C.
  * **UI:** gamepad-friendly action bars and navigation, written for 1.12.
  * **Shortcut:** a stand-alone input layer that turns controller input into 1.12 keybinds and mouse movement. It
    would give basic support with no UI work. Native support with prompts, flyouts and party d-pad targeting needs
    the client changes.

---

## 2. Other systems and rules

### 2.1 Rulesets instead of realms **[server]**
* Players pick a ruleset: Normal, PvP, Roleplaying or Hardcore, each one large world. Hardcore comes after launch.
  ([ForeverDB: rulesets and two-part names](https://foreverdb.net/article/rulesets-and-two-part-names))
* Character names are two-part, and one world spans many servers ("world instances"). This is mostly Blizzard's
  backend; for us at most a server-option and naming topic.

### 2.2 Camping **[server] [data] [assets]**
From [ForeverDB: Camping](https://foreverdb.net/camping):

* **Campfire kits** from Cooking: Basic (skill 1) allows 3 camp features, Journeyman (140) 5, Expert (220) 10.
  Players sitting or crafting nearby for 1 minute get the features' benefits.
* **Camp features**, one per profession at skill 20, upgraded at 140 and 300. Each is "mutually exclusive" with the
  matching class buff, and all share a 1-hour cooldown:

  | Profession | Skill 20 | Skill 140 | Skill 300 |
  |---|---|---|---|
  | Alchemy | Mana Well (mp5, as Blessing of Wisdom) | Fermenter (reagents) | Alchemy Laboratory |
  | Blacksmithing | Sharpening Wheel (Strength, as Strength of Earth) | Anvil | Master Forge |
  | Enchanting | Enchanted Lute (as Mark of the Wild) | Arcane Salvager (better disenchanting) | Arcane Forge |
  | Engineering | Reagent Bot (sells reagents) | Repair Bot | Anarchist's Workbench |
  | First Aid | First Aid Kit (Stamina, as Fortitude) | Toxin Study (potions, anti-venom) | Plague Doctor's Laboratory |
  | Fishing | Fish Bowl (+8% stats, as Kings) | Fishing Rack (uncommon fish, lures) | Fishing Hut (rare fish) |
  | Herbalism | Incense Candle (Intellect, as Arcane Intellect) | Greenhouse (grows herbs from seeds) | Seed Hybridizer |
  | Leatherworking | Camp Tent (rested XP up to 5% of a level) | Tanning Rack (reagents) | Sewing Machine |
  | Mining | Lodestone (attack power, as Might) | Rock Garden (spawns a mining node) | Molten Foundry |
  | Skinning | Camp Chair (+2% crit, as Moonkin Aura) | Field Guide (teaches Track Beasts) | Trapper's Workbench |
  | Tailoring | Faction Banner (Spirit, as Divine Spirit) | Spinning Wheel (reagents) | Loom |
  | Cooking | — | Cookie's Feast | Iron Oven (top recipes) |

* **Port assessment.** These are game objects and area auras. 1.12 can show them: spell focus objects and summoned
  objects already exist. The placement rules and the "sitting nearby for 1 minute" check are server logic. The
  object models are new **[assets]**.

### 2.3 Opt-in transmog **[client] [server]**
* Transmogrifiers in some cities let you build outfits. It can also be switched off to see everyone's real gear.
  ([Deep dive: camping, transmog, itemization](https://foreverdb.net/article/deep-dive-camping-transmog-itemization))
* **In the client:** `Blizzard_Transmog` (load-on-demand, 215 KB) on Retail's C_Transmog stack; tables
  TransmogOutfitEntry, TransmogOutfitSlotInfo, TransmogOutfitSlotOption, TransmogSituation* and TransmogSet. 1.12 has
  no display override per slot in its item code, so this needs client code: a visible-item override in the
  character display and update fields.

### 2.4 Stats: merged hit and crit, client-side stat tables **[server] [client]**
From the [veterans page](https://foreverdb.net/veterans) and [Stats explained](https://foreverdb.net/stats):

* **One hit stat and one crit stat.** Spell, melee and ranged hit share one stat; spell, melee and ranged crit are
  merged. This helps hybrids such as Retribution and Enhancement.
* **Gear ratings** at every level: 14 crit rating = 1% crit, 10 hit = 1%, 12 dodge = 1%, 15 parry = 1%, 5 block = 1%.
  1.12 has no ratings.
* **Crit per Agility and spell crit per Intellect** come from a client table (PlayerExpectedStat), as do base mana and
  attack power per stat (ChrClasses). Examples at level 30: a Warrior gets 1% crit from 10.4 Agility, a Mage gets 1%
  spell crit from 27.3 Intellect.
* **Other numbers:**
  * Hit needed against raid bosses: 8% melee and ranged, 17% spells, 27% when dual wielding.
  * 440 Defense makes you crit-immune against raid bosses.
  * Armor caps at 75% reduction.
* **Port assessment.** The merged stats and ratings are server formulas plus item stat types, and the 1.12 client's
  tooltips and character sheet would have to show them. Classic Era works these out on the server.

### 2.5 Riding and mounts **[data] [server]**
From [ForeverDB: Mounts](https://foreverdb.net/mounts):

* 141 mounts, 39 new.
* Mount items *teach* the mount, as from TBC on, instead of being used from the bag.
* One Riding skill: Apprentice (75) for 60% mounts, Journeyman (150) for 100% mounts. Most mounts cost less.
* Galestriders, the Skyborne cranes: every race can learn Galestrider Riding.
* **1.12 note.** Mounts as spells work in 1.12 (the spellbook shows them). Riding as one skill is a data and server
  change.

### 2.6 Professions **[data] [assets]**
From [ForeverDB: Professions](https://foreverdb.net/professions) and the veterans page:

* **Recipes.** 940 of 2,491 recipes are new and 732 changed: different reagents, more yield for oils and bombs,
  reworked skill-ups.

  | Profession | Recipes | New | Changed |
  |---|---|---|---|
  | Alchemy | 195 | 71 | 11 |
  | Blacksmithing | 506 | 197 | 186 |
  | Enchanting | 261 | 63 | 96 |
  | Engineering | 275 | 77 | 30 |
  | Leatherworking | 592 | 277 | 198 |
  | Tailoring | 471 | 180 | 209 |
  | Herbalism | 3 | 3 (Gardening) | 0 |
  | Mining | 18 | 5 (new bars) | 0 |

* **Healing potions** Minor through Major and anti-venoms moved to **First Aid**. Alchemists instead make stronger
  *Discolored Healing Potions* that carry a drawback.
* **Gathering professions craft:** Herbalism → Gardening, Skinning → Tanning, Mining → new bars.
* 385 new consumables. Blizzard says crafted food can give a small XP bonus.
  ([Consumables](https://foreverdb.net/consumables))

### 2.7 Racials (26 of 32 changed) **[data] [server]**
From [ForeverDB: Races](https://foreverdb.net/races). Examples:

* **Human:** Perception, *Ruthless* (+2% crit with spells and attacks), The Human Spirit, *Will to Survive* (removes
  stuns).
* **Orc:** Blood Fury (+10% attack power *and* spell power), Hardiness, *Savage* (+1% crit), *Shatter Curse*.
* **Dwarf:** *Big Game Hunter*, *Earth Fury*, Find Treasure, Stoneform (also −10% physical damage taken).
* **Night Elf:** *Elune's Light* (+10% crit for 15 sec), Quickness, Shadowmeld, Wisp Spirit.
* **Undead:** Cannibalize (also mana), *Touch of the Grave*, Underwater Breathing, Will of the Forsaken.
* **Tauren:** *Cultivation* (grow a harvestable copy of an herb), Endurance (also +2% crit bonus), *Plainsrunning*,
  War Stomp.
* **Gnome:** Engineering Specialization, Escape Artist, *Eureka!*, Expansive Mind.
* **Troll:** Beast Slaying, Berserking (+10% cast and attack speed), *Rapid Regeneration*, Regeneration.
* **Weapon racials** such as Human Sword Specialization now also help casters.
* **New race: Skyborne** (shen'dorei), playable for either faction in two kinds: High Order (Alliance) and Windshaper
  (Horde). It starts on Zephras Isle (1–12). Racials: Elemental Insight, *Read Leyline*, *Skysight* (both stronger
  near a leyline or elemental convergence), *Walk on Air* (glide), *Wind Blessed* (+3% haste). **[client] [assets]**
  A new race needs character-creation data, models (HD and SD), customisation and client race tables. 1.12 can't add
  races by data alone.

### 2.8 Hunter pets **[data]**
From [ForeverDB: Hunter pets](https://foreverdb.net/pets):

* 19 families, two of them new (Core Hound, Fox).
* Every family now has its own ability, for example Spider Web, Crab Pinch, Raptor Savage Rend, Gorilla Thunderstomp,
  Fox Trickster's Dance, Bird of Prey *Mine!* and Hyena Tendon Rip.
* Pets keep their own attack speed.
* The family bonuses to damage, armor and health are the same numbers as Era.

### 2.9 Reputation **[data]**
* 14 new factions: Theramore Expeditionary Force, Darkspear Raiders, Powderfuse, Barkskin Burrow, Bolder'ok Clan,
  Brotherhood of the Horse, Cenarion Scouts, Earthen Ring, Guardians of Hyjal, High Order, Kirin Tor, Nightclaw
  Druids, The Watchers and Windshapers.
* The client has 54 factions. ([Reputation](https://foreverdb.net/reputation))
* Factions are data in 1.12 (Faction.dbc), but the 1.12 client's reputation list has fixed limits; check before
  adding 14.

### 2.10 Travel **[data] [assets]**
* 22 new flight paths and 7 new flight masters. The client has 79 masters and 294 paths in all.
  ([Travel](https://foreverdb.net/travel))
* New ship, zeppelin and airship routes: Menethil–Southshore–Auberdine, Tanaris–Riverglades, Alterac
  Mountains–Zephras Isle, Thunder Bluff–Zephras Isle, Auberdine–Stormwind.
* Flight paths are DBC data plus server spawns. Boat routes need transport paths and objects.

---

## 3. Classes (summary)

The details are in [FOREVERDB_CLASS_CHANGES.md](FOREVERDB_CLASS_CHANGES.md). Headline numbers from
[ForeverDB: datamine](https://foreverdb.net/datamine):

* **38 new class abilities:** Hunter 12, Paladin 6, Priest 6, Mage 4, Shaman 4, Warrior 2, Druid 2, Rogue 1,
  Warlock 1. Examples:
  * Warrior: Tactical Mastery, Slam.
  * Paladin: Holy Strike, Seal and Judgement of Fury, Revoke.
  * Hunter: the 8 pet family abilities, Trickster's Dance, Aspect of the Beast, Lacerate, Thunderstomp.
  * Rogue: Coup de Grace.
  * Priest: Confounding Flash, Divine Grace, Chastise, Contingency Plan, Dark Sacrifice, Shadow Word: Death.
  * Shaman: Call of the Elements, Ancestors and Spirits; Totemic Recall.
  * Mage: Frost Blast, Arcane Wrath, Frostfire Bolt, Teleport: Dalaran.
  * Warlock: Curse of the Elements.
  * Druid: Revive, Lacerate.

  **[data]** for most (spell rows and trainer entries). Some need new spell effects or aura types that 1.12's spell
  system may not support **[client]**.
* **1,247 changed class spells,** often new numbers per rank. **[data]**
* **Removed or reworked**, per class ([datamine/removed](https://foreverdb.net/datamine/removed)):
  * Warrior: Sword and Polearm Specialization talents gone; Axe Specialization → Bloodthrill, Mace Specialization →
    Weaponmaster.
  * Paladin: no longer trains the Summon Warhorse and Charger spells or the old Judgement spells; Greater Blessing of
    Sanctuary is gone.
  * Shaman: Fire Nova Totem and Tranquil Air Totem gone; Weapon Mastery → Maelstrom Weapon; Totemic Mastery → Water
    Shield.
  * Warlock: Curse of Shadow gone; no longer trains Felsteed and Dreadsteed; several Improved-X talents reworked.
  * Druid: no longer trains Cat Form and Tiger's Fury as separate spells (reworked).
  * Every class: 60–156 changed tooltips.
* **Other rules:**
  * Rogues can use one-handed axes.
  * Shaman totems still need totem items.
  * Blizzard targets 10–15 seconds to kill a normal monster.
* **Relics.** Paladins, Shamans and Druids have 64 librams, totems and idols, 25 of them new
  ([finds](https://foreverdb.net/finds?tab=relics)). 1.12 already has the relic slot. **[data]**

## 4. Items and armor sets **[data] [assets]**

* **5,559 new items and 3,337 changed** ([datamine/items](https://foreverdb.net/datamine/items),
  [changed](https://foreverdb.net/datamine/changed)). Unique dungeon boss items are now blue. Dungeons drop hundreds
  of new items. Quest rewards cover more classes.
* **"Horizontal" gear:** 41 items stronger in one zone or against one enemy type, gathering without the profession,
  or walking on water ([finds](https://foreverdb.net/finds?tab=odd)). The zone- or target-conditional effects need
  server-side spell conditions.
* **34 new armor sets; 69 sets with new bonuses** ([datamine/sets](https://foreverdb.net/datamine/sets)). Among them:
  PvP Champion's and Lieutenant Commander's sets for new class and spec combinations; T1 and T2 bonus reworks (Battlegear
  of Valor, Magister's, The Elements and others).
* New items use 1.12-compatible item display data where they reuse old models. Items with new display IDs need the
  model and texture conversion. **[assets]**

## 5. World **[assets] [data]**

From [ForeverDB: World](https://foreverdb.net/datamine/world), [Dungeons](https://foreverdb.net/dungeons) and the BlizzCon
recap:

* **New zones:** Mount Hyjal, Zephras Isle (1–12), Darkspear Islands, Riverglades, Shen'dralas. Riverglades is built
  from an unused part of the Classic world: it connects the Burning Steppes, Redridge, the Swamp of Sorrows and the
  Badlands, with 150+ quests for the mid-30s to mid-40s.
* **New quests.** Over a thousand from 1 to 60, including new starting-zone quests. The Wetlands and Desolace get
  more.
* **New dungeons:**
  * The Hall of Thanes (13–20, Ironforge).
  * Ruins of Lordaeron (15–22).
  * Excavation Site: Wetlands (26–33).
  * City of Dalaran (28–35, Silverpine Forest).
  * Manor Mistmantle (Duskwood).
  * Also named in the client and in Legacy dungeon lists: Krol'dok Stronghold, The Drowned City, Alcaz Prison,
    Blackmaw Hold, The Shaper's Terrace.
* **New raids:** Hyjal Summit (20 players, 13 encounters) and the Barrow Deeps (10 players, 8 encounters).
* **Battlegrounds:** Darkspear Islands (15 vs 15, control points plus flag captures). In the client but not
  announced: Battle for Gilneas, Mak'gora Arena, Battle for Blackrock. A winter Warsong Gulch was added.
* **Our side.** Maps convert with our map converter (MAP_CONVERSION_DESIGN.md, MAP_SURVEY_160.md); Zephras Isle
  already converted in a dry run. Quests, creatures and spawns are server data the client does not hold.

## 6. Looks and interface **[client] [assets]**

From the BlizzCon recap and the beta notes:

* **Visual presets** (modern or Classic). **HD or SD character models**, with restored Classic animations on the HD
  models.
* **Rendering touches:** rivers flowing through Elwynn Forest, mist over Darkshore's waters, moonlight through
  Ashenvale.
* **UI refreshes:** updated nameplate selection display, Personal Resource Display textures, casting animations
  fixed for casters. The Damage Meter and Encounter Timeline addons are in the client too
  (`Blizzard_DamageMeter`, `Blizzard_EncounterTimeline`). Both depend on Edit Mode.
* **Our side.** DIFF_112_160.md has the engine half: lighting, fog, water and shadows need client code. HD models
  are new M2 versions. The SD models are close to 1.12's.

## 7. How the beta moved

[Build changelog](https://foreverdb.net/changelog): builds 70009 → 70450, 2026-09-29 to 10-16. The biggest single
build was 70170 (10-02): 1,038 spells, 60 talents, 526 items, 39 sets. Everything here is beta data and can still
change.

## 8. What the client can't tell us

ForeverDB notes that some things are decided by the server and can't be datamined:

* base crit, dodge per Agility, regen per Spirit;
* drop sources and vendors;
* quest givers, which are sometimes labelled "Classic data";
* creature spawns.

The same limit applies to our own client survey.

---

## Pick list (first pass)

A short view to start choosing from. Effort is rough: S = data only, M = data + server logic or assets,
L = client code.

| Change | Kind | Effort | Notes |
|---|---|---|---|
| Cooldown Manager | UI | **S–M** | A 1.12 addon or frame on existing cooldown and buff APIs, plus a move mode |
| Class spell tuning (1,247) | data | S each | Spell.dbc values plus server; pick per class |
| New items, sets, relics | data (+assets) | S–M | Data Studio import; new displays need art |
| Professions and consumables | data | S–M | Recipes, reagents, First Aid potions |
| Racial changes | data + server | M | Most are aura changes |
| Camping | server + assets | M | Objects, auras, placement rules |
| Riding as one skill, mounts as spells | data + server | M | |
| Reputation factions, flight paths | data | S–M | Check 1.12 client limits |
| New zones and maps | assets + data | M–L | Map converter; quests and spawns on the server |
| Legacy System | server + client UI | **L** | A 1.12-native rebuild; Retail's traits engine can't be reused |
| Merged hit and crit, ratings | server + client | L | Formulas plus tooltips and character sheet |
| Transmog | client + server | L | Visible-item override in the client |
| Gamepad support | client engine + UI | **L** | Controller input in the exe; a keybind layer is a shortcut |
| Skyborne race | client + assets | L | New race tables, models, character creation |
| Visual presets, HD models, rendering | client engine | L | See DIFF_112_160.md section on graphics |
