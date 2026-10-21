# WoW Forever (1.60.1.70450) class changes vs Classic

Source: ForeverDB class "changes vs Classic" pages (`https://foreverdb.net/class/<Class>/changes`), data from the WoW Forever beta client build 1.60.1.70450, extracted 2026-10-19.

## Read this first

- **The baseline is Classic Era 1.15.9.69722, not 1.12.** In most cases that is the same as 1.12 data, but a few Classic Era values may differ from your VMaNGOS 1.12 tables. Check your own `spell_template` before porting a number.
- **The beta's level cap is 30.** Higher ranks exist in the client but can't be reached yet, so top-rank numbers may still change.
- **No spell IDs.** The pages don't list spell IDs, except that Season of Discovery (SoD) spells use IDs from 395000 up. New abilities appear below by name and level only.
- **Low-level coefficient penalty removed.** Forever gives ranks learned below level 20 their full spell power coefficient, so many "higher coefficient" notes below come from this one rule.
- **"Worked out by the server".** For some AoE spells the Forever client has no damage number, so the server calculates it. Those spells are flagged below.
- **"Tooltip only".** Many entries say the tooltip text changed while the spell-data numbers stayed the same. Some of those tooltips show different numbers anyway. The page explains that Forever tooltips read their numbers from spell data, while Classic tooltips often had the numbers typed in. Treat those as possible formula or scaling changes that the page could not see.
- **SoD leftovers.** Each class page lists Season of Discovery spells that are still in the Forever client files but that nothing teaches. The page labels them "Speculation: probably unused". They are listed per class for completeness.

**What the tags mean** (how to port to 1.12):
- **[data]**: a change to numbers, costs, durations, ranks or flags on existing spell or talent rows, which a VMaNGOS server can apply. For tooltips to match, patch the client Spell.dbc as well.
- **[client]**: needs 1.12 client code or UI work, such as a new mechanic, a new aura behaviour, a new debuff category or a new UI element.
- **[both]**: new spells or talents (which need new rows in the client DBCs plus server support), talent-tree layout moves (client Talent.dbc), or cases where it's unclear.

---

## Warrior

**Page counts:** 10 new talents, 22 new or changed spells, 36 changed talents, 5 Classic spells/talents removed, 9 SoD leftovers.

### New abilities
- **Tactical Mastery** (trainer ability): now a baseline ability. Classic's Tactical Mastery talent became the Arms talent "Improved Tactical Mastery". [both]
- **Victory Rush** (level 20, was an SoD rune): an instant attack for 1 + 15% of attack power that heals you for 10% of max health. You can use it within 20 sec of killing a non-trivial enemy, and it has a 30 sec cooldown. The SoD version did 45% of AP, healed 30% and had an 8 sec cooldown. [both]

### Talent tree changes
New talents:
- **Lingering Rage** (Fury, replaces Improved Demoralizing Shout): rage starts decaying 10 sec later after you leave combat. [both]
- **Furious Precision** (Fury, replaces Improved Cleave): +10% off-hand hit chance. [both]
- **Master of Defense** (Protection, replaces Improved Shield Block): gain 5 rage when you dodge or parry with a shield equipped. [both]
- **Raging Blows** (Fury tier 4, 1 rank): Cleave and Whirlwind cost 3 less rage. [both]
- **Spearing Strike** (Arms tier 4, active): deals 40% weapon damage, or 120% total against Giants, Dragonkin and mounted targets, and dismounts the target. [both]
- **Vanguard** (Protection tier 4): Charge is usable in Defensive Stance. [client]
- **Bloodthrill** (Arms, replaces Axe Specialization): main-hand hits on a target with your Rend have a 20% chance to enable Overpower. [client]
- **Focused Rage** (Protection tier 5, 3 ranks): offensive abilities cost 3 less rage. [both]
- **Weaponmaster** (Arms, replaces Mace Specialization): a bonus that depends on weapon type. Axe/Polearm get +5% crit, Mace/Staff ignore 15% of armor, and Sword gets a 5% chance for an extra attack. [both]
- **Gore Drinker** (Fury tier 6, 2 ranks): after Enrage, Berserker Rage, Bloodrage, Death Wish or Bloodthirst, your next 3 melee attacks each restore 1% of max health. [both]

Reworked talents:
- **Booming Voice**: shouts get +50% area of effect and cost 25% less rage. It no longer extends shout duration. [data]
- **Improved Bloodrage**: all rage from Bloodrage +50%, instead of +5 instant rage. It moved to tier 1. [both]
- **Iron Will**: stun and fear durations are 15% shorter, instead of +15% resist against stun and charm. It moved to tier 1. [both]
- **Shield Specialization**: blocks now generate 5 rage instead of 1. [data]
- **Anticipation**: +20 defense instead of +10. It moved to tier 2. [data]
- **Improved Revenge**: Revenge damage +60%, replacing the 45% stun chance. [data]
- **Improved Tactical Mastery**: keeps an extra 15 rage on top of baseline Tactical Mastery. [data]
- **Improved Thunder Clap**: moved to Protection. Thunder Clap costs 6 less rage instead of 4. [both]
- **Unbridled Wrath**: 60% chance for +1 rage, up from 40%. [data]
- **Anger Management**: now also generates 1 rage every 3 sec in combat. [data]
- **Blood Craze**: also triggers when a single hit takes more than 20% of your max health. [client]
- **Defiance**: 3 ranks instead of 5. Gives +15% threat in Defensive Stance, but only with a shield equipped. [data]
- **Improved Disarm**: cuts Disarm's cooldown by 20 sec instead of adding 3 sec to its duration. [data]
- **Dual Wield Specialization**: off-hand attacks also generate 50% more rage. [data]
- **Enrage**: 30% chance to gain +10% physical damage for 12 sec after taking any damaging hit. Classic gave +25% for 12 swings after being crit. [data]
- **Impale**: works in every stance, not only the three stances. [data]
- **Two-Handed Weapon Specialization**: 3 ranks, +3% (was 5 ranks, +5%). [data]
- **Death Wish**: the drawback is now +5% damage taken, instead of -20% armor and resistances. [data]
- **Improved Berserker Rage**: also removes movement-impairing effects. It moved to tier 5. [both]
- **Improved Shield Wall**: cuts Shield Wall's cooldown by 11 min instead of adding 5 sec to its duration. [data]
- **Bastion** (was a one-hand bonus): +10% all damage while a shield is equipped. [data]
- **Flurry**: +25% attack speed (was +30%). It moved to tier 6. [data]
- **Improved Slam**: moved to Arms tier 6 with 2 ranks. Cuts Slam's cast time and global cooldown by 0.5 sec and its cooldown by 3 sec. Slam no longer resets or delays your swing. [client]
- Wording-only or tier moves: Cruelty and Deep Wounds (wording), Improved Overpower (tier 2), Improved Execute and Improved Shield Bash (tier 4), Improved Heroic Strike, Improved Charge, Improved Sunder Armor, Last Stand, Piercing Howl, Concussion Blow (wording). [both]

Removed talents: Improved Battle Shout, Improved Taunt, Polearm Specialization, Sword Specialization (folded into Weaponmaster) and Toughness. [data]

### Notable changed spells
Buffs and debuffs:
- **Battle Shout** ranks 1-7: about 40% weaker (rank 7 gives 139 AP instead of 232), lasts 3 min instead of 2 and now covers the raid. [data]
- **Demoralizing Shout** ranks 1-5: about 40% stronger (rank 5 is -196 AP instead of -140) and lasts 45 sec instead of 30. [data]

Damage:
- **Revenge** ranks 1-6: about 70% more damage (rank 6 does 138-168 instead of 81-99). [data]
- **Shield Slam** ranks 1-4: about 87% more damage (rank 4 does 640-670 instead of 342-358) and causes "very high" threat. [data]
- **Bloodthirst** ranks 1-4: now 45% of AP plus 30/37/43/48 flat. It also gives +10% movement speed for 10 sec and no longer heals on your next 5 hits. [both]
- **Whirlwind**: now hits with both melee weapons. [client]
- **Cleave**: the tooltip now says it hits "a second nearby enemy" instead of the target's nearest ally. [data]

Cooldowns, durations and usability:
- **Thunder Clap**: cooldown 6 sec (was 4). Usable in Battle or Defensive Stance. Attack-speed slow is 20% (was 10%). [data]
- **Taunt**: cooldown 8 sec (was 10). [data]
- **Shield Block**: lasts 7 sec (was 5) and blocks 2 attacks (was 1). [data]
- **Shield Wall**: -60% damage taken (was -75%) for 12 sec (was 10), now against all attacks, with a 15 min cooldown (was 30). [data]
- **Retaliation**: cooldown 15 min (was 30). [data]
- **Slam**: new rank 1 at level 20 (the Classic ranks move up one) and an 18 sec cooldown on every rank, where Classic had none. [data]
- **Berserker Rage**: learned at level 30 instead of 32. [data]
- **Recklessness**: the tooltip now says "+100% crit chance". [data]

Tooltip only (same spell data): Bloodrage no longer says it keeps you in combat. Defensive Stance now spells out +30% threat. Intimidating Shout has an 8 yd radius and Challenging Shout a 10 yd radius. Berserker Stance has wording changes only. [data]

### Mechanic and system changes
- Rage economy is the central theme: rage-cost cuts (Focused Rage, Raging Blows, Booming Voice), rage gains (Master of Defense, Shield Specialization, Anger Management, Dual Wield Specialization) and a longer out-of-combat decay delay (Lingering Rage). [both]
- Weapon specializations are merged into one talent that checks weapon type (Weaponmaster). [both]
- Stance restrictions are loosened: Thunder Clap works in Defensive Stance, and Charge does too with Vanguard. [data]
- SoD leftovers (Speculation: probably unused): Commanding Shout, Devastate, Enraged Regeneration, Meathook, Quick Strike, Raging Blow, Rallying Cry, Shockwave, Valor of Azeroth.

---

## Paladin

**Page counts:** 18 new talents, 42 new or changed spells, 30 changed talents, 8 Classic spells/talents removed, 14 SoD leftovers.

### New abilities
- **Holy Strike**: new. The page gives no details, but talents refer to its damage, threat and Judgement-refresh effects. [both]
- **Seal of Fury**: new seal. Its talent mentions a "shield" that absorbs damage. [both]
- **Judgement of Fury**: new judgement that goes with Seal of Fury. [both]
- **Revoke**: new. No details given. [both]
- **Hammer of the Righteous** (level 40, was an SoD rune): Holy damage to the target and up to 3 nearby enemies equal to 3x main-hand DPS (SoD: 4x). Shares its cooldown with Crusader Strike. [both]

### Talent tree changes
New talents:
- **Holy Conduit** (Retribution, replaces Improved Seal of the Crusader): Consecration, Holy Wrath, Exorcism and Hammer of Wrath cost 40% less mana. [both]
- **Improved Seal of Fury** (Protection tier 3): when Seal of Fury's shield is fully absorbed, it restores mana, more if the attacker is higher level. [both]
- **Purifying Power** (Holy, replaces Improved Lay on Hands): Cleanse and Purify cost 20% less, and Exorcism and Holy Wrath cooldowns are 33% shorter. [both]
- **Reverence** (Holy tier 3, 3 ranks): 30% of mana regen continues while casting. [both]
- **Sacred Duty** (Protection tier 3): +4% stamina, and 60 sec off the cooldowns of Divine Shield, Divine Protection and Templar's Bulwark. [both]
- **Sanctified Judgement** (Retribution tier 3): Judgement refunds 60% of the judged seal's mana cost. [both]
- **Voice of Truth** (Holy tier 3, active): immune to silence and interrupt for 6 sec. [both]
- **Infusion of Light** (Holy tier 4): Holy Shock or Flash of Light crits cut your next Holy Light's cast time by 1 sec. [both]
- **Sacred Arbiter** (Retribution, replaces Improved Retribution Aura): Holy Strike +20% damage, and it refreshes all Judgements on the target. [both]
- **Swift Judgement** (Protection tier 4, active): resets Judgement's cooldown and makes the next Judgement free. [both]
- **Consecrated Ground** (Holy, replaces Lasting Judgement): Holy spells do +10% damage to the first 4 enemies that enter your Consecration. [client]
- **Divine Precision** (Holy tier 5): +18% hit with Holy spells. [both]
- **Templar's Bulwark** (Protection, replaces Blessing of Sanctuary): an absorb shield equal to 100% of max health for 8 sec. Applies Forbearance. [both]
- **Champion of the Light** (Retribution tier 6): spell damage +30% of Intellect. [both]
- **Instrument of Law** (Retribution tier 6): Hammer of Wrath casts 1 sec faster, and -20% threat while Righteous Fury is off. [both]
- **Iron Creed** (Protection tier 6, 5 ranks): Holy Strike +25% threat. With Righteous Fury on, Holy Strike also gives -10% damage taken for 6 sec. [both]
- **Light's Vigil** (Holy tier 7): a 30 sec mark on a target. Your next Holy Shock on it has no cooldown and either deals 175-189 to an enemy (refunding 75% of the mark's cost) or heals the ally's whole party for 315-333. One per party. [client]
- **Twist of Light** (Retribution tier 7): seals cost 20% less. Swapping away from Command, Righteousness, Fury or Justice leaves an "Echo" that applies the old seal on your next swing. [client]

Reworked talents:
- **Benediction**: -10% mana cost on all instant spells and abilities (was -15% on Judgement and Seals only). [data]
- **Redoubt**: damaging melee hits have a 10% chance to give +20% block (was +30% block after being crit). [data]
- **Anticipation**: +20 defense (was +10). [data]
- **Healing Light**: now also boosts Holy Shock. [data]
- **Improved Seals** (renamed from Improved Seal of Righteousness): 3 ranks, +15% damage to all seals and judgements. [data]
- **Precision**: +3% hit with all attacks, not just melee. [data]
- **Spiritual Focus**: 2 ranks instead of 5, and now also covers Light's Vigil. [data]
- **Unyielding Faith**: fear and disorient durations 30% shorter (was +10% resist). [data]
- **Improved Righteous Fury**: -6% damage taken while Righteous Fury is on (was +50% threat). [data]
- **Pursuit of Justice**: +15% movement speed (was +8%). [data]
- **Shield Specialization**: blocks also restore 4% of max mana, at most once every 3 sec. [client]
- **Vindication**: now lowers the target's AP and raises yours by 3% for 30 sec (was -15% strength and agility). [data]
- **Eye for an Eye**: now any crit (not just spell crits) reflects 10% (was 30%). [data]
- **Illumination**: refund cut to 50% of base cost (was 100%). Now also covers Light's Vigil. [data]
- **Reckoning**: 40% chance after a block, or 100% after a non-periodic crit taken. Stores up to 4 extra attacks. Block procs are limited to once per 1.5 sec. [client]
- **Vengeance**: 3 ranks, +3% physical and Holy damage for 30 sec after a non-periodic crit, stacking 3 times (was +15% for 8 sec). [data]
- **Holy Power**: +15% crit for Holy Shock and Holy Strike, +5% for other spells. [data]
- **Holy Shield**: 110 damage per block (was 65). [data]
- **One-Handed Weapon Specialization**: 3 ranks instead of 5, moved to tier 4. [both]
- Tier moves only: Deflection, Toughness (tier 1), Conviction (wording), Divine Favor, Improved Hammer of Justice, Repentance. Guardian's Favor and Improved Judgement have wording changes only. [both]
- Talents that became trainer spells: **Blessing of Kings** (trainer level 20), **Consecration** (trainer level 20). Holy Shock remains a talent, moved to tier 5. [both]

Removed talents: Improved Blessing of Might, Improved Blessing of Wisdom, Improved Concentration Aura, Improved Devotion Aura and Sanctity Aura. [data]

### Notable changed spells
Blessings and buffs (all now last 1 hour):
- **Blessing of Might** ranks 1-7 and **Greater Blessing of Might**: about 28% weaker (rank 7 gives 133 AP instead of 185). Duration 1 hr (was 5 or 15 min). [data]
- **Blessing of Wisdom** ranks 1-6 and **Greater Blessing of Wisdom**: about 20% stronger (40 mp5 instead of 33). Duration 1 hr. [data]
- **Blessing of Kings**, **Greater Blessing of Kings**, **Blessing of Light**, **Greater Blessing of Light**: duration 1 hr. Greater Blessing of Light's old rank 1 entry is gone. [data]
- **Salvation** (renamed from Blessing of Salvation) and **Mass Salvation** (renamed from Greater Blessing of Salvation): 1 hr, and no longer called a Blessing in the tooltip. [both]
- **Righteous Fury**: +90% Holy threat (was +60%). [data]
- Resistance auras (Shadow, Frost, Fire): now affect raid members too. [data]

Damage and healing:
- **Exorcism** ranks 1-6: about 6-13% less damage (rank 6 does 475-529 instead of 505-563). [data]
- **Hammer of Wrath** ranks 1-3: about 6-10% less (rank 3 does 474-522 instead of 504-556). [data]
- **Holy Wrath**: now also stuns Undead and Demons for 2 sec. [data]
- **Holy Shock**: new rank 1 at level 30 (now 4 ranks, at levels 30/40/48/56). Cooldown 10 sec (was 30). Rank 1 deals 129-139 or heals 110-118. [data]
- **Consecration**: the client has no damage number (the server calculates it). The tooltip says a reduced 96 base over 8 sec, plus 216 extra to the first 4 enemies that enter. [client]
- **Seal of Righteousness**: the tooltip now shows Holy spell power scaling per hit (+7.65% to +12%). **Judgement of Righteousness**: coefficient 0.429 (was 0.5) and no longer trainer-taught. [data]
- **Holy Light** (rank 9: 1495-1665 instead of 1590-1770) and **Flash of Light** (rank 6: 286-320 instead of 343-383): the tooltips show lower heals, but the page says the spell-data numbers are the same. [both]

Defensives:
- **Divine Shield**: no longer slows your attacks. Instead you deal 50% less damage. [data]
- **Divine Protection**, **Blessing of Protection**, **Divine Shield**: now apply a named **Forbearance** debuff (1 min) and can't be cast while it is active. [client]
- **Lay on Hands**: cooldown 20 min (was 1 hr). No longer stops mana regeneration. [data]

Judgements:
- **Judgement**: the tooltip says it does not consume the seal. [client]
- **Judgement of the Crusader**: stronger (161 Holy instead of 140 at rank 6) and lasts 40 sec (was 10). Judgements of **Light** and **Wisdom**: 40 sec (was 10). [data]
- Judgements of Righteousness, Crusader, Command, Justice, Light and Wisdom, plus Sense Undead, are no longer trainer-taught. They exist only as effects of other abilities. [both]

### Mechanic and system changes
- **Forbearance** becomes a named, shared debuff across Divine Shield, Divine Protection, Blessing of Protection and Templar's Bulwark. [client]
- Judgement no longer consumes the seal, and judgement debuffs last 40 sec. [client]
- Seal-swap "Echo" mechanic (Twist of Light). [client]
- Intellect becomes spell power (Champion of the Light), and mana regen while casting arrives for Paladins (Reverence). [both]
- Class mounts: **Summon Warhorse** and **Summon Charger** are no longer Paladin abilities (still in the files). **Greater Blessing of Sanctuary** is gone completely. [data]
- SoD leftovers (Speculation: probably unused): Enhanced Blessings, Exorcist, Seal of Martyrdom, Avenging Wrath, Divine Light, Divine Sacrifice, Divine Storm, Horn of Lordaeron, Inspiration Exemplar, Judgement of Martyrdom, Rebuke, Righteous Judgement, Sacred Shield, Shield of Righteousness.

---

## Hunter

**Page counts:** 15 new talents, 40 new or changed spells, 29 changed talents, 5 Classic spells/talents removed, 13 SoD leftovers.

### New abilities
- New pet abilities, with no details on the page: **Dismember**, **Dust Cloud**, **Mine!**, **Pinch**, **Savage Rend**, **Swipe**, **Tendon Rip**, **Web**. [both]
- **Trickster's Dance**: new, no details. [both]
- **Lacerate** (was an SoD rune): now a class ability, no details. [both]

### Talent tree changes
New talents:
- **Improved Tracking** (Survival tier 1, renamed from Monster Slaying, 5 ranks): +5% damage to whatever creature type you are tracking. [client]
- **Careful Aim** (Marksmanship tier 2, 5 ranks): +100% of Intellect as attack power. [both]
- **Focused Fire** (Beast Mastery, replaces Improved Eyes of the Beast): you and your pet deal +2% damage while the pet is out. [both]
- **Improved Stings** (Marksmanship tier 2): Serpent Sting +20% damage, Viper Sting cooldown -6 sec, Scorpid Sting +45 sec duration. [both]
- **Lone Wolf** (Marksmanship tier 3): +20% damage while you have no pet. [both]
- **Rapid Killing** (Marksmanship tier 3): Rapid Fire cooldown -2 min. Killing a non-trivial enemy, or one dying with your Serpent Sting on it, gives +20% to your next Shot. [both]
- **Predator's Edge** (Survival tier 4, 5 ranks): melee ability crit damage +30% and off-hand damage +50%. [both]
- **Summon Hawk** (Beast Mastery tier 4, active): a hawk dive-bombs the target for 32 + 5% ranged AP and keeps attacking for 18 sec. At most 2 at a time. Shares a cooldown with Arcane Shot. [client]
- **Expose Prey** (Survival, replaces Killer Instinct): hits on a Hunter's Marked target have a 10% chance to enable Mongoose Bite. [client]
- **Rapid Recuperation** (Marksmanship tier 5): Serpent Sting gives 50% regen while casting for 15 sec, or 100% when Rapid Killing is consumed. [both]
- **Resourcefulness** (Survival tier 5): traps and melee abilities cost 60% less mana. Crits give 50% regen while casting for 30 sec. [both]
- **Strider Kick** (Survival tier 5, active): a 100% weapon damage kick that gives +30% movement speed for 3 sec. [both]
- **Survivalist's Discipline** (Survival tier 5): trap and Deterrence cooldowns 40% shorter. [both]
- **Lacerating Strikes** (Survival, replaces Wyvern Sting): Mongoose Bite adds a bleed for 40% of its damage over 21 sec. [both]
- **Sniper Shot** (Marksmanship tier 7, active): long-range shot for ranged damage + 160. Your next 3 Shots get +10 yd range for 10 sec. [both]

Reworked talents:
- **Deadly Aspects**: Aspect of the Hawk gives Auto Shot a 10% chance (was 5%) to proc +30% ranged speed. New: Aspect of the Beast gives melee hits the same proc for melee speed. [data]
- **Endurance Training**: also gives the pet +15% armor. [data]
- **Lethal Attacks** (renamed from Lethal Shots): +5% crit with all attacks, not just ranged. [data]
- **Efficiency**: -15% mana on Shots, Stings and melee abilities (was -10% on Shots and Stings). [data]
- **Entrapment**: triggered traps always root for 5 sec (was a 25% chance). [data]
- **Improved Aspect of the Monkey**: 3 ranks, +6% dodge, and the pet gets half the bonus. [both]
- **Savage Strikes**: +4% crit on all melee abilities (was +20% on Raptor Strike and Mongoose Bite). [data]
- **Improved Arcane Shot**: -1.5 sec cooldown (was -1), but it doesn't affect abilities that share Arcane Shot's cooldown. [data]
- **Surefooted**: movement-impairing effects 30% shorter (was +15% resist). [data]
- **Unleashed Fury** and **Ferocity**: weaker (+15% damage and +10% crit, from 20% and 15%), but they now also apply to hawks. [data]
- **Improved Mend Pet**: also -20% Mend Pet cost. [data]
- **Mortal Shots**: +30% crit damage on all ranged abilities (was ranged weapon). [data]
- **Survival Tactics**: +10% hit on traps and Feign Death (was -10% resist). [data]
- **Barrage**: +10% to Multi-Shot, Aimed Shot and Volley (was +15% to Multi-Shot and Volley). [data]
- **Bestial Discipline**: also lets 50% of mana regen continue while casting. [data]
- **Intimidation**: the pet's stun hit also gets +100% crit chance. [data]
- **Spirit Bond**: 1% health every 5 sec (was 2% every 10). [data]
- **Lightning Reflexes**: +10% agility (was +15%). [data]
- **Bestial Swiftness**: pet speed no longer limited to outdoors. [data]
- **Improved Wing Clip**: 3 ranks, moved to tier 2. Moves only: Hawk Eye, Pathfinding, Survivalist, Improved Mend Pet, Barrage, Scatter Shot. [both]
- Talents that became trainer spells: **Aimed Shot** (trainer level 20). Counterattack and Trueshot Aura remain talents but moved to tier 4. [both]

Removed talents: Improved Feign Death, Improved Hunter's Mark, Improved Scorpid Sting, Thick Hide. Improved Serpent Sting is "retired": still in the data but placed off the grid so it can't be learned. [data]

### Notable changed spells
Shots and stings:
- **Arcane Shot** ranks 1-8: about 15-50% more damage (rank 8 does 217 instead of 183). [data]
- **Aimed Shot**: now trainer-taught at level 20. Cast time 2 sec (was 3). Bonus damage cut about 70% (rank 6: weapon + 166 instead of + 600). Shares its cooldown with Multi-Shot. [data]
- **Multi-Shot**: a single rank (was 5) costing 13.9% of base mana (was 100 mana). Cooldown 6 sec (was 10). Shares its cooldown with Aimed Shot. [data]
- **Serpent Sting** ranks 1-8: about 15-50% less damage (rank 8: 415 over 15 sec instead of 490). [data]
- **Scorpid Sting**: single rank costing 9% of base mana. Now -2% hit chance (was -20 strength and agility). [data]
- **Viper Sting**: new 15 sec cooldown. [data]
- **Volley**: no cooldown (was 1 min). The client has no damage number (server-calculated; tooltip 112 per sec instead of 80). You don't need to face the target. [both]
- **Hunter's Mark**: ranks 1-3 stronger (rank 3: 98 instead of 75), rank 4 weaker (71 instead of 110). [data]

Melee:
- **Raptor Strike** ranks 4-8: bonus damage roughly halved (rank 8: weapon + 70 instead of + 140). Rank 1 costs 10 mana (was 15). [data]
- **Mongoose Bite** ranks 1-4: now weapon damage + 15/22/37/57 (was a flat 25-115) and needs a melee weapon. [data]
- **Counterattack**: now 50% weapon damage + 26/40/70/110, with a new rank. Needs a melee weapon. Moved to tier 4. [data]
- **Rapid Fire**: also speeds up melee attacks. [data]

Aspects and auras:
- **Aspect of the Hawk** rank 6: 55 ranged AP (was 110). [data]
- **Aspect of the Beast**: 4 ranks (levels 30/40/50/60) giving +50/70/90/110 melee AP on top of being untrackable. [data]
- **Aspect of the Monkey**: range field changed to 100 yd (was self). [data]
- **Aspect of the Wild**: now affects the raid. [data]
- **Trueshot Aura**: now 5 ranks from level 25 (30/40/50/75/50 AP; the page lists rank 5 as 50). Ranged AP only, now raid-wide, and no 30 min duration in the tooltip. Moved to tier 4. [data]

Traps:
- **Freezing**, **Frost**, **Explosive** and **Immolation Trap** (rank 1): cooldown 30 sec (was 15). The "out of combat only" restriction is gone from the tooltip. The limit is now one trap per school (one Fire, one Frost) instead of one in total. [both]

Pet spells:
- **Demoralizing Screech** (renamed from Screech): -198 AP for 30 sec (was -100 for 4 sec). [data]
- **Furious Howl**: tooltip 82 extra damage, lasting 1 min (was 45-57 for 10 sec). **Lightning Breath** and **Scorpid Poison**: tooltips show lower numbers with the same data. **Thunderstomp**: new rank 4 (level 50). [data]

Other:
- **Feign Death**: now says you actually die if you don't cancel it within 6 min. [client]
- **Disengage**: disables auto-attack instead of leaving combat. [data]
- **Eyes of the Beast**: 2 min (was 1). [data]

### Mechanic and system changes
- A melee and off-hand Hunter path: Aspect of the Beast now gives AP, plus Predator's Edge, Strider Kick, Lacerating Strikes, Expose Prey, and melee weapon requirements on Mongoose Bite and Counterattack. [both]
- Intellect becomes attack power (Careful Aim), and mana regen while casting (Bestial Discipline, Rapid Recuperation, Resourcefulness). [both]
- Shared cooldowns: Aimed Shot with Multi-Shot, Summon Hawk with Arcane Shot. [client]
- A pet-less build option (Lone Wolf) and a hawk pseudo-pet (Summon Hawk). [client]
- Some costs become percentage of base mana (Multi-Shot, Scorpid Sting). [data]
- SoD leftovers (Speculation: probably unused): Aspect of the Viper, Carve, Chimera Shot (and its Scorpid, Serpent and Viper versions), Cobra Slayer, Flanking Strike, Heart of the Lion, Kill Command, Enchanted Flare, Lava Breath, Aspect of the Falcon.

---

## Rogue

**Page counts:** 9 new talents, 10 new or changed spells, 34 changed talents, 4 Classic spells/talents removed, 10 SoD leftovers.

### New abilities
- **Coup de Grace**: new, no details. [both]
- **One-Handed Axes**: Rogues can now learn the weapon skill from level 1. [both]

### Talent tree changes
New talents:
- **Dirty Tricks** (Subtlety tier 2): Sap and Blind cost 50% less energy. [both]
- **Puncturing Wounds** (Combat, replaces Improved Backstab): Backstab +30% crit and Mutilate +15% crit. Backstab has a 45% chance for an extra combo point. [both]
- **Improved Distract** (Subtlety tier 3): +5 yd Distract radius, and distracted enemies detect stealth as if 2 levels lower. [client]
- **Flawless Execution** (Combat, replaces Dagger Specialization): Eviscerate costs 10 less energy. [both]
- **Mutilate** (Assassination tier 5, was SoD): hits with both weapons for 75% damage + 17 each. +20% against poisoned targets. 2 combo points. Costs 60 energy (SoD: 40). [both]
- **Cutthroat** (Subtlety, replaces Deadliness): Backstab has a 15% chance to let your next Ambush within 10 sec work without stealth. [client]
- **Quietus** (Subtlety tier 6): Sinister Strike, Ghostly Strike and Hemorrhage do +10% below 35% health. [both]
- **Thousand Cuts** (Subtlety tier 7): each Rupture tick makes your next Hemorrhage or Backstab 3 energy cheaper, stacking 5 times. [client]
- **Venom** (Assassination tier 7, finisher): poisons +30% damage and +10% application chance, for 9-21 sec depending on combo points. [both]

Reworked talents:
- **Camouflage**: now reduces stealth's speed penalty by 15% and its cooldown by 6 sec. Moved to tier 1. [both]
- **Improved Eviscerate**: moved to Combat tier 1, +20% (was +15%). [both]
- **Improved Gouge**: moved to Assassination tier 1. [both]
- **Malice**: crit bonus now also applies to poisons. [data]
- **Master of Deception**: 3 ranks, stealth as if 3 levels higher. [data]
- **Opportunity**: 2 ranks, +10% to Backstab, Garrote, Ambush and Mutilate (was +20%, 5 ranks). [data]
- **Remorseless Attacks**: triggers on any non-trivial kill and also covers Mutilate. [data]
- **Deflection**: 3 ranks, +6% parry (was 5 ranks, +5%). [data]
- **Murder**: +4% against Humanoids and Giants (was +2% against four types). [data]
- **Precision**: 3 ranks, +3% hit with all attacks (was 5 ranks, +5% melee). [data]
- **Setup**: 100% chance (was 45%). Moved to tier 2. [both]
- **Endurance**: Sprint and Evasion cooldowns 60% shorter (was a flat 1.5 min). [data]
- **Ghostly Strike**: 180% weapon damage with a main-hand dagger. [data]
- **Improved Expose Armor**: Expose Armor costs 10 less energy and refunds 2 combo points at 5 points, instead of +50% armor reduction. [client]
- **Initiative**: 100% chance (was 75%). [data]
- **Lethality**: +20% crit damage (was +30%), now including Mutilate. [data]
- **Cold Blood**: now also works on Mutilate. Moved to tier 4. [both]
- **Dual Wield Specialization**: +25% off-hand damage (was +50%). [data]
- **Heightened Senses**: stealth detection as if 3 levels higher. Moved to tier 4. [both]
- **Improved Poisons**: also a 50% chance not to use a poison charge. [client]
- **Premeditation**: usable out of stealth, combo points last 20 sec (was 10). Moved to tier 4. [both]
- **Serrated Blades**: a flat 9% armor ignore (was level-scaled). [data]
- **Dirty Deeds**: Garrote no longer needs you behind the target. Moved to tier 5. [both]
- **Hack and Slash** (was a sword proc): bonus by weapon type. Axe/Sword get a 5% extra-attack chance, Dagger/Fist get +5% crit, Mace ignores 15% of armor. [both]
- **Hemorrhage**: 100% weapon damage (145% with a dagger). Instead of the +3 physical damage debuff, it now gives +15% Rupture damage. [both]
- **Improved Kidney Shot**: 2 ranks. Stunned targets take +10% from your attacks and poisons (was +9% from all sources). [data]
- **Vigor**: 2 ranks, moved to tier 5. **Aggression**: now also boosts Backstab. **Weapon Expertise**: -2% chance to be dodged or parried (was +5 weapon skill). [data]
- Moves or wording only: Elusiveness, Improved Ambush, Blade Flurry, Vile Poisons, Seal Fate. [both]

Removed talents: Fist Weapon Specialization, Improved Sap, Mace Specialization, Sleight of Hand. [data]

### Notable changed spells
- **Expose Armor** ranks 1-5: about 12-33% stronger (rank 5: 450 armor per combo point instead of 340, so 2250 at 5 points). [data]
- **Rupture** ranks 1-6: about 40% less damage (rank 6 at 5 points: 469 over 16 sec instead of 800). [data]
- **Sap**: no longer needs stealth, and doesn't break stealth. [data]
- **Distract**: also lowers enemy stealth detection by 1 level for 10 sec. [client]
- **Eviscerate** and **Backstab**: spell data is unchanged, but the tooltips show lower numbers (Backstab rank 9 shows +150 instead of +225, Eviscerate rank 6 shows 377-421 at 5 points instead of 407-451). The page says Classic tooltips had typed-in numbers. [both]
- **Gouge**: wording only. [data]

### Mechanic and system changes
- Mutilate and Venom bring a dual-wield poison path. Poisons can crit (Malice). [both]
- Stealth detection is expressed as "levels" (Master of Deception, Heightened Senses, Distract, Improved Distract). [client]
- Weapon specializations are merged into Hack and Slash by weapon type. Mace Specialization and Fist Weapon Specialization are removed. [both]
- SoD leftovers (Speculation: probably unused): Main Gauche, Between the Eyes, Blade Dance, Poisoned Knife, Quick Draw, Redirect, Saber Slash, Shadowstrike, Shiv, Shuriken Toss.

---

## Priest

**Page counts:** 14 new talents, 39 new or changed spells, 36 changed talents, 3 Classic spells/talents removed, 16 SoD leftovers.

### New abilities
- **Confounding Flash**, **Divine Grace**, **Chastise**, **Contingency Plan**, **Dark Sacrifice**: new, no details on the page. [both]
- **Shadow Word: Death** (was an SoD rune): now a class ability, no details. The Early Demise talent refers to it. [both]

### Talent tree changes
New talents:
- **Power in Light** (Discipline tier 1): Smite and Penance do +10% to targets with your Holy Fire or SW: Pain. [both]
- **Twin Disciplines** (Discipline tier 1): +5% damage and healing on instant spells. [both]
- **Holy Precision** (Discipline, replaces Improved Power Word: Fortitude): +18% hit with Holy spells. [both]
- **Improved Mind Flay** (Shadow tier 3): each Mind Flay tick does 20% more than the one before. [client]
- **Binding Heal** (Holy tier 4, was SoD): heals the target and yourself for 223-271 at rank 1, costing 155 mana, with ranks up to 757-893. [both]
- **Dark Inspiration** (Shadow tier 4): Mind Blast or a fully channelled Mind Flay gives you and your party +15% spirit for 10 sec. [both]
- **Soul Warding** (Discipline tier 4): Power Word: Shield cooldown -4 sec and cost -15%. [both]
- **Devouring Contagion** (Shadow, replaces Improved Vampiric Embrace): Devouring Plague costs 50% less and spreads to 2 nearby enemies if the target dies. [client]
- **Litany of Light** (Holy, replaces Improved Prayer of Healing): casting a different heal from your last one refunds 10% of base cost. [client]
- **Penance** (Discipline tier 5, was SoD): a channelled volley of 42 Holy damage or 126 healing per tick over 2 sec. 150 mana, 36 yd range. [both]
- **Renewed Hope** (Discipline tier 5): heals get +10% crit on targets with Weakened Soul and cut its remaining duration by 5 sec. [client]
- **Divine Aegis** (Discipline, replaces Force of Will): non-periodic crit heals leave a shield worth 15% of the heal (up to 600 at level 30, growing with level). [client]
- **Early Demise** (Shadow tier 6): SW: Death +30% crit at or below 20% health. [both]
- **Prayer of Mending** (Holy tier 7, was SoD): a heal of 172 that triggers when the target is damaged, then jumps up to 5 times. 210 mana. [both]

Reworked talents:
- **Shadow Focus**: +5% hit with Shadow spells (was -10% resist chance). Moved to tier 1. [data]
- **Spirit Tap**: also triggers when an enemy with your Vampiric Embrace dies. [client]
- **Twilight Focus** (renamed from Healing Focus): 3 ranks, and the 70% pushback protection now covers all spells. [data]
- **Wand Specialization**: 2 ranks. Moved to tier 1. [both]
- **Mental Strength**: +10% Intellect (was +10% max mana). [data]
- **Shadow Reach**: 2 ranks, affects all offensive Shadow spells. [data]
- **Silent Resolve**: 3 ranks, -30% threat, and stun, fear and silence 15% shorter. [data]
- **Blessed Recovery**: also triggers on a hit taking more than 30% of max health, and refreshes roll leftover healing forward. [client]
- **Improved Power Word: Shield**: +20% absorb (was +15%). [data]
- **Inner Focus**: crit bonus only applies to non-periodic spells. [data]
- **Inspiration**: triggers on any non-periodic crit heal. [data]
- **Meditation**: 50% regen while casting (was 15%). [data]
- **Mental Agility**: 3 ranks, and now also reduces Smite and Holy Fire cost. [data]
- **Improved Healing**: also covers Penance and Prayer of Mending. [data]
- **Improved Inner Fire**: +45% armor and +12 charges. [data]
- **Improved Mana Burn**: -1.0 sec cast (was -0.5). [data]
- **Searing Light**: +5% Holy damage, plus a 10% chance per Holy Fire tick to make your next Holy Nova free (was +10% to Smite and Holy Fire). [client]
- **Shadow Weaving**: 3 ranks, +2% per stack and only for your own Shadow damage (was +3% for everyone). [data]
- **Vampiric Embrace**: lasts 30 sec (was 1 min). [data]
- **Silence**: also interrupts spellcasting for 3 sec. [data]
- **Spirit of Redemption**: lasts 15 sec (was 10). [data]
- **Spiritual Guidance**: healing +25% of Spirit, but damage only +8% of Spirit (was 25% for both). [data]
- **Spiritual Healing**: 3 ranks, and boosts all healing spells. [data]
- **Shadowform**: +10% Shadow damage (was 15%). Shadow spells cost 50% less mana and get +100% crit damage. Now it only blocks healing spells, so Smite, Holy Fire and Resurrection are usable in Shadowform. [both]
- Moves or wording only: Blackout, Divine Fury, Improved Mind Blast, Improved SW: Pain, Martyrdom, Improved Psychic Scream, Darkness, Power Infusion. [both]
- Talents that became trainer spells: **Divine Spirit** (trainer level 30, lasts 1 hr) and **Lightwell** (trainer level 40). [both]

Removed talents: Improved Fade, Shadow Affinity, Unbreakable Will. [data]

### Notable changed spells
Buffs:
- **Power Word: Fortitude** ranks 1-6 and **Prayer of Fortitude**: about 30% stronger (70 stamina instead of 54) and last 1 hr (was 30 min). Prayer of Fortitude now covers the raid. [data]
- **Prayer of Spirit** and **Prayer of Shadow Protection**: now cover the raid. Prayer of Shadow Protection's old rank 1 entry is removed. [data]
- **Fear Ward**: lasts 3 min (was 10) with a 3 min cooldown (was 30 sec). [data]
- **Elune's Grace**: reworked into one rank costing 3% of base mana. Gives -50% chance to be hit by melee and ranged for 15 sec or 3 misses. [data]

Damage:
- **Smite** ranks 2-8: much less damage, about 55% less at the top (rank 8: 160-180 instead of 371-415). [data]
- **Holy Fire** ranks 1-8: about 35-50% less (rank 8: 184-232 + 75 over 10 sec instead of 355-449 + 145). [data]
- **Starshards** ranks 1-7: about 92% more damage (rank 7: 1800 over 6 sec instead of 936), with a new 30 sec cooldown. [data]
- **Shadow Word: Pain** ranks 2-8: about 10-25% less (rank 8: 762 instead of 852) with a higher coefficient (0.2 instead of 0.167). [data]
- **Mind Blast** ranks 1-9: about 6-15% less (rank 9: 472-498 instead of 503-531). [data]
- **Mind Flay** ranks 1-6: about 8-19% less (rank 6: 390 instead of 426) with a higher coefficient. [data]
- **Devouring Plague** ranks 1-6: about 6-16% less, but the cooldown is 24 sec (was 3 min). [data]
- **Holy Nova** ranks 1-6: about 4-10% less damage and healing. [data]
- **Hex of Weakness**: now -204 melee AP at rank 6 (was -20 damage). **Touch of Weakness**: lower damage (rank 6: 56 instead of 64), and its debuff now reduces AP (rank 1: -43) instead of damage. [data]
- **Feedback** ranks 1-5: about 60% cheaper (rank 5: 230 mana instead of 580). [data]
- **Shadowguard**: the tooltip shows 96 instead of 116 with the same data. [both]

Healing:
- **Lesser Heal** ranks 2-3: slightly more healing (rank 3: 151-175 instead of 135-157) and a 0.714 coefficient. [data]
- **Heal** ranks 1-4: about 14-18% less (rank 4: 611-691 instead of 712-804). [data]
- **Flash Heal** ranks 1-7: about 7-13% less (rank 7: 757-893 instead of 812-958). [data]
- **Greater Heal** ranks 1-5: about 6-12% less (rank 5: 1853-2067 instead of 1966-2194). [data]
- **Renew** ranks 2-10: about 15-35% less (rank 10: 830 over 15 sec instead of 970). [data]
- **Prayer of Healing** ranks 1-5: about 40% less (rank 5: 631-667 instead of 1041-1099). Now cast on a target at 40 yd range, healing that target's party. [both]
- **Desperate Prayer** ranks 1-7: about 2-8% less. [data]
- **Power Word: Shield** ranks 2-10: about 1-3% weaker (rank 10: 928 instead of 942). [data]

Utility:
- **Fade** ranks 1-6: 5x the threat reduction (rank 6: -4100 instead of -820). [data]
- **Levitate**: mounting now cancels it. [data]

### Mechanic and system changes
- Discipline becomes an absorb-and-instant tree: Penance, Divine Aegis, Renewed Hope, Soul Warding, Twin Disciplines. [both]
- Shadowform rework: cheaper Shadow spells, Shadow crits do double damage, and Holy damage spells are allowed. [both]
- Holy spell damage is heavily lowered, but Starshards is greatly buffed. [data]
- Prayer of Healing becomes a targeted party heal. [both]
- SoD leftovers (Speculation: probably unused): Increased Fortitude, Secrets of Elune, Secrets of Undeath, Secrets of the Light, Secrets of the Loa, Circle of Healing, Dispersion, Eye of the Void, Homunculi, Mind Sear, Mind Spike, Pain Suppression, Power Word: Barrier, Shadowfiend, Vampiric Touch, Void Plague.

---

## Shaman

**Page counts:** 13 new talents, 38 new or changed spells, 32 changed talents, 7 Classic spells/talents removed, 7 SoD leftovers.

### New abilities
- **Fire Nova** (was an SoD rune; replaces the removed Fire Nova Totem): instant Fire damage to enemies within 10 yd of your active Fire totem. Range 30 yd, and no Fire Totem item needed. Rank 5 deals 226-252 for 375 mana (SoD: 396-442 for 520). [both]
- **Totemic Projection** (level 22, keeps its SoD spell ID): moves your totems. Costs 25% of base mana, 1 min cooldown (SoD: 10 sec). [client]
- **Totemic Recall**: new, no details. [both]
- **Call of the Elements**, **Call of the Ancestors**, **Call of the Spirits**: new, no details on the page. [both]

### Talent tree changes
New talents:
- **Mental Dexterity** (Enhancement tier 2): +100% of Intellect as attack power. [both]
- **Mindfulness** (Restoration tier 2): 50% regen while casting. [both]
- **Shamanistic Focus** (Enhancement, replaces Two-Handed Axes and Maces): Shocks and Lightning Shield cost 45% less. [both]
- **Water Shield** (Restoration, replaces Totemic Mastery): 3 water globes. Each hit taken, or each spell crit you land, restores 2% of max mana. It's an Elemental Shield, so only one can be active. [both]
- **Earthbound** (Elemental tier 4): Earthbind Totem roots nearby targets for 5 sec when you drop it. [client]
- **Improved Stormstrike** (Enhancement tier 5): Stormstrike gives 50% regen while casting for 15 sec, and its cooldown resets when you dodge or parry. [client]
- **Lava Burst** (Elemental tier 5, was SoD): 107-139 Fire damage at rank 1 (SoD: 469-605), 2.5 sec cast, 10 sec cooldown, 135 mana. +20% if your Flame Shock is on the target. [both]
- **Lightning Overload** (Elemental tier 5): Lightning Bolt and Chain Lightning have a 10% chance to cast a free half-damage copy. [client]
- **Mental Quickness** (Enhancement tier 5): spell damage and healing +30% of Intellect. [both]
- **Spirit Weapons** (Enhancement tier 5, renamed from Parry, replaces Improved Weapon Totems): gives parry. Threat -30% without Rockbiter Weapon, or +30% with it. [both]
- **Maelstrom Weapon** (Enhancement, replaces Weapon Mastery): melee hits stack a buff (up to 5) that cuts Lightning Bolt's cast time and cost by 20% per stack. [client]
- **Rage of the Farseer** (Enhancement tier 7, active): +30% attack speed for 25 sec. [both]
- **Riptide** (Restoration tier 7, was SoD): heals 456-504 plus 445 over 15 sec, 245 mana. Chain Heal on that target +25%. [both]

Reworked talents:
- **Ancestral Knowledge**: +10% Intellect (was +5% max mana). [data]
- **Concussion**: Earth Shock is the only Shock it still boosts. [data]
- **Convection**: also covers Lava Burst. [data]
- **Earth's Grasp**: moved from Elemental to Enhancement. [both]
- **Thundering Strikes**: +5% crit with all spells and attacks. [data]
- **Tidal Focus**: also +5% hit. [data]
- **Call of Flame**: also +15% to Flame Shock, Fire Nova and Lava Burst, and Flame Shock lasts 9 sec longer. [data]
- **Elemental Alacrity** (renamed from Lightning Mastery): moved to tier 2 with 3 ranks. -0.5 sec cast on Lightning Bolt, Chain Lightning and Lava Burst (was -1 sec on LB and CL). [both]
- **Elemental Devastation**: triggers only on direct-damage spell crits. Moved to tier 2. [both]
- **Improved Ghost Wolf**: -3 sec cast, and usable indoors. [client]
- **Improved Reincarnation**: also +4% max health. [data]
- **Natural Grace** (renamed from Healing Grace): -15% threat on all spells. [data]
- **Totemic Focus**: also cheapens spells that summon or move totems. [data]
- **Anticipation**: 3 ranks, +6% dodge. **Healing Focus**: 3 ranks. [data]
- **Eye of the Storm**: -70% pushback on Lightning Bolt, Chain Lightning and Lava Burst (was a Focused Casting proc). [data]
- **Flurry**: +25% speed (was 30%), and melee crits only. [data]
- **Restorative Totems**: Healing Stream Totem +50% (was +25%). [data]
- **Stormstrike**: now strikes for weapon damage and boosts your next Lightning Bolt, Chain Lightning or Earth Shock by 20% (was an extra attack plus +20% to the next 2 Nature hits). Moved to tier 4. [both]
- **Tidal Mastery**: healing crit only, no longer lightning. [data]
- **Toughness**: +10% stamina (was +10% item armor). [data]
- **Trial by Fire**: Fire Nova +20% damage and -4 sec cooldown, Magma Totem +20 sec. [data]
- **Elemental Reach**: also covers Fire Nova and Lava Burst, and Totemic Projection's cooldown -20 sec. [data]
- **Healing Way**: flat +25% Healing Wave (was a stacking debuff). [data]
- **Elemental Fury**: now 5 ranks, moved to tier 6. [both]
- **Elemental Mastery**: 15 sec buff. Lightning Bolt and Lava Burst cast 0.5 sec faster, and Chain Lightning has no cooldown (was a free guaranteed crit). [client]
- **Mana Tide Totem**: moved to tier 4. Now 4 ranks at levels 25/38/48/58: the Classic ranks are learned earlier (rank 3 at 48 instead of 58), and rank 4 is new. Half the cost (30 mana instead of 60 at rank 3), but restores 88 instead of 170 per 3 sec. 30 yd. [both]
- Moves only: Ancestral Healing, Elemental Warding, Elemental Weapons, Improved Reincarnation, Reverberation (wording). [both]

Removed talents: Call of Thunder, Enhancing Totems, Nature's Guidance, Shield Specialization. [data]

### Notable changed spells
Damage:
- **Lightning Bolt** ranks 4-10: about 30-55% less damage (rank 10: 185-207 instead of 419-467). Coefficient 0.714 (was 0.857). Cast time 2.5 sec (was 3). About 17% cheaper. [data]
- **Chain Lightning** ranks 1-4: about 55-77% less damage (rank 4: 116-130 instead of 493-551). Coefficient 0.571 (was 0.714). Cast time 2 sec (was 2.5). About 48% cheaper. [data]
- **Earth Shock** ranks 3-7: about 20-43% less (rank 7: 293-309 instead of 517-545). [data]
- **Flame Shock** ranks 1-6: about 5-45% less (rank 6: 166 + 176 over 12 sec instead of 292 + 320). [data]
- **Frost Shock** ranks 1-4: about 30-43% less (rank 4: 275-291 instead of 486-514). [data]
- Tooltips lower with the same data: Lightning Shield 178 instead of 198, Magma Totem 73 instead of 75, Frostbrand 158 instead of 175. [both]

Healing:
- **Healing Wave** ranks 3-10: about 7-17% less (rank 10: 1508-1722 instead of 1620-1850). [data]
- **Lesser Healing Wave** ranks 1-6: about 7-15% less (rank 6: 775-865 instead of 832-928). [data]
- **Chain Heal** ranks 1-3: about 17-27% less (rank 3: 458-522 instead of 551-629). [data]

Totems (all now 5 min, 30 yd range, and most now include raid members):
- Stoneskin, Strength of Earth, Tremor, Healing Stream, Mana Spring, Poison Cleansing, Disease Cleansing, Frost/Fire/Nature Resistance, Flametongue, Windfury, Windwall and Grace of Air totems: duration 5 min (was 1 or 2 min) and radius 30 yd (was 20). [data]
- Value changes on totems: **Strength of Earth** 53 strength (was 77). **Grace of Air** 89 agility (was 77). **Windfury Totem** 246 AP (was 315). **Healing Stream** 11 per tick (was 14). **Flametongue Totem** 17-55 (was 15.8-48.7). **Stoneskin** now reduces Physical damage (was melee). [data]

Weapon imbues (all now last 60 min instead of 5):
- **Rockbiter**, **Flametongue**, **Frostbrand** and **Windfury Weapon**: duration 60 min. A main-hand Flametongue or Windfury Weapon stops you personally benefiting from the matching totem. [client]

Other:
- **Ancestral Fortitude**: range field changed to 100 yd (was self). [data]

### Mechanic and system changes
- **Dual Wield can't be learned** in Forever. The skill that Classic Era gave from level 1 is gone. [both]
- **Fire Nova Totem** and **Tranquil Air Totem** are removed (Fire Nova became a spell). [both]
- Totem quality-of-life: Totemic Projection, Totemic Recall, the "Call of ..." spells, longer and larger totems. [client]
- The weapon-imbue and totem anti-stacking rule is new. [client]
- Spell damage and healing from Intellect (Mental Quickness), AP from Intellect (Mental Dexterity), and mana regen while casting (Mindfulness, Improved Stormstrike, Water Shield). [both]
- SoD leftovers (Speculation: probably unused): Healing Rain, Ancestral Guidance, Decoy Totem, Earth Shield, Lava Lash, Molten Blast, Spirit of the Alpha.

---

## Mage

**Page counts:** 5 new talents, 30 new or changed spells, 40 changed talents, 1 Classic spell/talent removed, 12 SoD leftovers.

### New abilities
- **Frost Blast**: new. No details, but Improved Frostbolt and Arctic Reach now affect it. [both]
- **Arcane Wrath**: new. No details, but Missile Barrage and Improved Channeling refer to it. [both]
- **Frostfire Bolt** (level 40, was an SoD rune): 92-108 plus 27 over 9 sec, 40% slow. Hits the lower of Frost and Fire resistance. 205 mana. SoD rank 1 did 325-379. [both]
- **Teleport: Dalaran**: new. [both]

### Talent tree changes
New talents:
- **Missile Barrage** (Arcane tier 1): Arcane Blast (40%), Arcane Missiles hits (10%) and Arcane Wrath, Fireball, Frostbolt or Frostfire Bolt (20%) can make your next Arcane Missiles free, half the duration and firing every 0.5 sec. [client]
- **Arcane Blast** (Arcane tier 3, was SoD): 30-34 Arcane damage, 1.5 sec cast, 15% of base mana. Each cast stacks +15% of Intellect as spell damage and +175% cost, up to 4 stacks. [client]
- **Ice Lance** (Frost tier 3, was SoD): 26-30 Frost damage at rank 1, +200% against frozen targets. [both]
- **Heating Up** (Fire tier 4): non-periodic crits from Fireball, Frostfire Bolt, Fire Blast and Scorch cut your next Pyroblast's cast time by 25% per stack (up to 3). Fire Blast adds a stack every time it deals damage. [client]
- **Fingers of Frost** (Frost tier 5): chill effects have a 15% chance to make your next 2 spells treat the target as frozen. [client]

Reworked talents:
- **Arcane Focus** and **Elemental Precision**: now spell hit, +5% (was resist reduction). Elemental Precision has 5 ranks. [data]
- **Improved Channeling**: also 70% pushback protection on Arcane Blast and Arcane Wrath. [data]
- **Improved Fire Blast**: 2 ranks. -2 sec cooldown and +15 yd range. Moved to tier 1. [both]
- **Improved Fireball**: now also affects Frostfire Bolt. **Improved Frostbolt**: now also affects Frost Blast. [data]
- **Incineration** (renamed from Incinerate): 3 ranks, +6% crit to Fire Blast, Ice Lance, Arcane Blast and Scorch. [data]
- **Arcane Subtlety**: -15 resistance (was -10), and -30% Arcane threat (was -40%). [data]
- **Arcane Resilience**: 2 ranks, moved to tier 2. **Impact**: 3 ranks. **Shatter**: 3 ranks. **Burning Soul**: 3 ranks. [both]
- **Magic Absorption**: 2 ranks, a full resist refunds 2% mana (was 5%). [data]
- **Permafrost**: chill durations +33% (was +3 sec). [data]
- **Arcane Geometry**: +6 yd range on Arcane spells (was +50% to Amplify and Dampen Magic). [data]
- **Arcane Impact**: +6% crit on all Arcane spells (was Arcane Explosion only). [data]
- **Improved Blizzard**: the chill slow is now 40% (was 65%). [data]
- **Arcane Meditation**: 50% regen while casting (was 15%). [data]
- **Arcane Shielding**: Mana Shield 33% more efficient (was 20%), and Mage Armor resistances +50%. [data]
- **Arctic Reach**: also covers Frost Blast. [data]
- **Arcane Mind**: +10% Intellect (was +10% max mana), and +100% Arcane crit damage. [data]
- **Cold Snap**: moved to tier 5. [both]
- **Critical Mass**: 5 ranks, +10% Fire crit (was 3 ranks, +6%). [data]
- **Fire Power**: 3 ranks, +3% Fire damage per Fire effect of yours on the target, up to 12%. Mixed-school effects don't count. [client]
- **Winter's Chill**: the debuff now raises your Cone of Cold damage against the target by 40%, stacking up to 5 times (was +2% Frost crit chance per stack). [client]
- **Combustion**: counts only non-periodic crits. [data]
- **Improved Scorch**: the Fire vulnerability now applies only to your own Fire damage. [data]
- **Arcane Instability**, **Arcane Power**, **Master of Elements**, **Improved Counterspell**, **Frost Warding**, **Ice Block**: wording only. [data]
- Moves only: Wand Specialization, Frostbite, Frost Channeling, Improved Cone of Cold. [both]

Removed talents: none. The only removed item is Detect Magic (see below).

### Notable changed spells
Damage:
- **Fireball** ranks 2-12: about 16-35% less (rank 12: 425-541 + 60 over 8 sec instead of 596-760 + 76). [data]
- **Frostbolt** ranks 2-11: about 10-26% less (rank 11: 457-493 instead of 515-555). [data]
- **Pyroblast** ranks 1-8: about 27-35% less (rank 8: 520-646 + 212 instead of 716-890 + 268). [data]
- **Scorch** ranks 1-7: about 30-37% less (rank 7: 163-193 instead of 233-275). [data]
- **Fire Blast** ranks 2-7: about 7-13% less (rank 7: 402-474 instead of 431-509). [data]
- **Arcane Explosion** ranks 1-6: about 4-9% less. **Frost Nova** ranks 2-4: about 3% less. **Blast Wave** ranks 1-5: about 2-4% less. [data]
- **Flamestrike** ranks 1-6: the spell-data entry no longer lists the DoT part. The tooltip still shows 332 over 8 sec at rank 6 (was 340). Only one Flamestrike per Mage at a time. [both]
- **Blizzard**: the client has no damage number (server-calculated). The tooltip shows 1168 instead of 1192 at rank 6. [both]
- **Cone of Cold** ranks 1-5: about 3-6% less damage. Slow is 40% for 6 sec (was 50% for 8). The first 4 targets get an extra 10% slow for 3 more sec. [client]
- **Arcane Missiles**: the tooltip shows 209 per sec instead of 230 at rank 8, with the same data. [both]

Utility and defensive:
- **Amplify Magic** and **Dampen Magic**: about 50% stronger (Amplify rank 4: 113 instead of 75; Dampen rank 5: 135 instead of 90). [data]
- **Fire Ward**, **Frost Ward**, **Ice Barrier**: about 1-2% weaker absorbs. The Ice Barrier tooltip now says spellcasts are not "delayed" either. [data]
- **Mage Armor**: 50% regen while casting (was 30%). [data]
- **Arcane Intellect**: 1 hr (was 30 min). **Arcane Brilliance** now covers the raid, and its old rank 1 entry is removed. [data]
- **Remove Lesser Curse**: now also removes Banes. [client]
- **Blink**: frees you from Immobilize effects (wording). [data]
- **Chilled**: 1 rank instead of 3. **Conjure Water**: new rank 8. [data]

### Mechanic and system changes
- Procs that change how spells work: Fingers of Frost (counts as frozen), Missile Barrage, Heating Up, and Arcane Blast stacks. [client]
- Spell hit replaces resist-reduction talents. [data]
- **Detect Magic** is removed. [data]
- SoD leftovers (Speculation: probably unused): Expanded Intellect, Living Flame, Arcane Barrage, Arcane Surge, Balefire Bolt, Frozen Orb, Icy Veins, Living Bomb, Mass Regeneration, Regeneration, Rewind Time, Spellfrost Bolt.

---

## Warlock

**Page counts:** 17 new talents, 32 new or changed spells, 34 changed talents, 13 Classic spells/talents removed, 16 SoD leftovers.

### New abilities
- **Portal of Summoning** (level 60, keeps its SoD spell ID): costs 100% of base mana plus 5 Soul Shards, 1 hr cooldown (SoD: 12% mana, 1 shard, 2 min). [both]

### Talent tree changes
New talents:
- **Demonic Aegis** (Demonology tier 2): Demon Skin and Demon Armor +30%. [both]
- **Demonic Energies** (Demonology tier 2): your spell damage heals your pet for 15%, and Life Tap gives the pet 100% of the mana you gain. [client]
- **Improved Drains** (Affliction tier 2): Drain Life, Drain Soul and Wrack +20%. [both]
- **Molten Skin** (Destruction tier 2, 5 ranks): -10% all damage taken. [both]
- **Soul Harvest** (Affliction, replaces Improved Drain Soul): killing a non-trivial target with your Drain Soul gives +100% mana regen and 100% regen while casting for 10 sec. [both]
- **Pandemic** (Affliction tier 3): +100% crit damage on Corruption, Banes, drains, Siphon Life and Wrack. [both]
- **Decimation** (Demonology tier 4): Soul Fire cooldown -90%. Shadow Bolt or Searing Pain on a target below 35% gives +6% damage, a faster Soul Fire and no shard cost for 10 sec. [client]
- **Demonic Brand** (Demonology tier 4): Searing Pain has 50% less threat and brands the target. Your pet's next 6 attacks on it add Fire or Shadow damage. [client]
- **Malevolence** (Affliction tier 4, 5 ranks): +5% Shadow crit. [both]
- **Bane of Havoc** (Destruction, replaces Improved Immolate): a 5 min Bane that copies 15% of your damage on other targets onto the banished target. One target only. [client]
- **Demonic Knowledge** (Demonology tier 5): you and your demon get spell damage equal to 100% of your level while a demon is out. [both]
- **Fire and Brimstone** (Destruction tier 5): Conflagrate +25% crit. [both]
- **Improved Felhunter** (Demonology, replaces Improved Subjugate Demon): +30% to Felhunter Tainted Blood, Devour Magic and Paranoia. Spell Lock cooldown -6 sec. [both]
- **Shadow and Flame** (Destruction, replaces Emberstorm): Conflagrate gives +10% Shadow damage and Shadowburn gives +10% Fire damage for 20 sec. Conflagrate doesn't consume Immolate, and Shadowburn refunds its shard. [client]
- **Demonic Pact** (Demonology tier 7): summoning a different demon no longer cancels Demonic Sacrifice. [client]
- **Incinerate** (Destruction tier 7, was SoD): 90-104 Fire damage, +25% on an Immolated target, 2.5 sec cast, 205 mana. SoD did 222-258. [both]
- **Wrack** (Affliction, replaces Dark Pact): channelled, 36 Shadow per sec for 6 sec, and the target takes +10% from your other Shadow DoTs. [both]

Reworked talents:
- **Bane**: now also covers Incinerate. Moved to tier 1. [both]
- **Demonic Embrace**: +15% stamina with no spirit penalty. [data]
- **Improved Corruption**: also +10% Corruption damage. [data]
- **Improved Health Funnel**: +40% transfer (was +20%), -30% health cost, no threat, and usable whatever your demon's health. [client]
- **Improved Imp**: no longer boosts Blood Pact. [data]
- **Improved Shadow Bolt**: +20% Shadow damage from you for a flat 12 sec (was 4 charges, shared by all casters). [client]
- **Suppression**: +5% hit and -20% threat (was -10% resist). [data]
- **Unholy Power**: +10% to all damage from all demons, Imp included (was +20% melee only). Moved to tier 1. [both]
- **Aftermath**: Immolate's initial damage +50%, and Conflagrate always dazes. [data]
- **Cataclysm**: 3 ranks, -10% Destruction cost. [data]
- **Fel Vitality** (renamed from Fel Intellect): 3 ranks, pet health and mana +15%, and your max mana +15%. [data]
- **Amplify Curse**: now boosts Bane of Agony. [data]
- **Demonic Sacrifice**: buffs swapped to the "opposing" aspect (Imp gives Shadow, Succubus gives Fire, Voidwalker gives mana, Felhunter gives health). Lasts 2 hr (was 30 min). Moved to tier 3. [data]
- **Fel Concentration**: 3 ranks, also covers Wrack and cast-time drains. [data]
- **Improved Bane of Agony** (renamed): 2 ranks, +10%. [data]
- **Ruin**: now 5 ranks, moved to tier 3. [both]
- **Agonizing Flames** (renamed from Improved Searing Pain): 3 ranks, and also +10% to all Destruction spell damage. [data]
- **Curse of Exhaustion**: -30% speed (was -10%). Moved to tier 4. [data]
- **Intensity**: 3 ranks, protects all Destruction casts and channels. [data]
- **Nightfall**: also procs from Drain Soul and Wrack. [data]
- **Pyroclasm**: Rain of Fire and Hellfire roll the stun chance over their duration. [client]
- **Soul Siphon** (renamed from Improved Drain Life): 3 ranks, +12% per Affliction effect on the target (up to 36%) for Drain Life, Drain Soul and Wrack. [client]
- **Master Demonologist**: Imp gives +10% Fire damage, Succubus +10% Shadow, Felhunter -10% Magic damage taken. Voidwalker unchanged. [data]
- **Shadowburn** and **Conflagrate**: see below. They stay talents, at tiers 3 and 4. [both]
- Moves only: Destructive Reach, Improved Life Tap, Grim Reach, Master Summoner, Fel Domination, Soul Link, Shadow Mastery, Improved Voidwalker (wording). [both]

Removed talents: Devastation, Fel Stamina, Improved Curse of Exhaustion, Improved Curse of Weakness, Improved Drain Mana, Improved Firebolt, Improved Firestone, Improved Healthstone, Improved Lash of Pain, Improved Spellstone. [data]

### Notable changed spells
Damage (the biggest caster nerf of any class):
- **Shadow Bolt** ranks 3-10: about 25-53% less (rank 10: 253-283 instead of 482-538). [data]
- **Immolate** ranks 1-8: about 25-47% less (rank 8: 158 + 275 over 15 sec instead of 279 + 510). [data]
- **Corruption** ranks 2-7: about 28-47% less (rank 7: 438 over 18 sec instead of 822), with a higher coefficient. [data]
- **Bane of Agony** (renamed from Curse of Agony) ranks 1-6: about 15-47% less (rank 6: 552 instead of 1044). Coefficient 0.133 (was 0.083). [both]
- **Bane of Doom** (renamed from Curse of Doom): 1742 instead of 3200. Coefficient 4 (was 1). [both]
- **Searing Pain** ranks 1-6: about 40-50% less (rank 6: 105-123 instead of 204-240). [data]
- **Soul Fire** ranks 1-2: about 46% less (rank 2: 383-479 instead of 703-881). [data]
- **Shadowburn** ranks 1-6: about 30-47% less (rank 6: 251-281 instead of 450-502). Range 30 yd (was 20). Shard window 8 sec (was 5). [data]
- **Conflagrate**: about 44-47% less (top rank: 251-313 instead of 447-557), with new low ranks. Moved to tier 4. [data]
- **Drain Life** ranks 2-6: about 18-32% less (rank 6: 255 over 5 sec instead of 355). [data]
- **Siphon Life** ranks 1-4: about 9-27% less. **Death Coil** ranks 1-3: about 3-5% less and about 1-6% more expensive. **Hellfire**: about 1% less. [data]
- **Drain Soul**: ranks 1-2 stronger, 3-4 weaker (rank 4: 420 instead of 455). Shards can now drop from damage on non-trivial targets and always drop on kills. [client]
- **Rain of Fire**: the client has no damage number (server-calculated). The tooltip shows 880 instead of 904. [both]

Curses:
- **Curse of Recklessness** ranks 1-4: pure armor reduction (rank 4: -505 armor). It no longer raises the target's AP, and the old -640 armor is gone. [data]
- **Curse of the Elements**: weaker (-60 resistance instead of -75 at rank 3, +4% damage at rank 1), cheaper (150 mana instead of 200), learned earlier (rank 3 at level 40 instead of 60), with a new rank 4 at level 50. [data]
- **Curse of Weakness** ranks 1-6: about 20% stronger (-37 instead of -31), but physical damage only. [data]
- **Curse of Tongues**: wording only. **Curse of Idiocy**: no longer trainer-taught. [data]

Self, pet and utility:
- **Life Tap**: the tooltip now scales with Spirit (+100% of Spirit to both health and mana). [data]
- **Health Funnel** ranks 1-7: an extra healing component of about +40% (rank 7: 1530 + 612 over 10 sec), with reduced threat. [data]
- **Demon Skin** and **Demon Armor**: wording ("increases health regeneration"). [data]
- **Create Healthstone**, **Create Soulstone**, **Create Firestone**, **Create Spellstone**, **Detect Invisibility**: each merged into one ranked spell (5/5/4/3/3 ranks) instead of separate spells. The Major Healthstone heals 1440 (was 1200). [both]
- **Firestone** is now a weapon enchant: +2% spell crit and up to +21 Fire damage (was an off-hand item with a melee proc). [client]
- **Spellstone** is now a weapon enchant: +2% spell haste and up to +21 Shadow damage (was a use-item absorb plus 1% crit). [client]

### Mechanic and system changes
- **Banes** become a new debuff category, separate from Curses ("one Bane per Warlock per target"). Agony, Doom and Havoc are Banes, and Remove Curse and Remove Lesser Curse now also remove Banes. [client]
- **Soul Shards** come from non-trivial targets and can drop on damage, not only on the kill (Drain Soul, Shadowburn). [client]
- Firestone and Spellstone become weapon imbues. Spell haste appears as a stat. [client]
- **Curse of Shadow** is removed completely. **Summon Felsteed** and **Summon Dreadsteed** are no longer Warlock abilities (still in the files). [data]
- SoD leftovers (Speculation: probably unused): Dance of the Wicked, Demonic Grace, Chaos Bolt, Demon Charge, Demonic Howl, Fel Armor, Grimoire of Synergy, Invocation, Menace, Metamorphosis (and its Demon Charge and Demonic Howl versions), Shadow Cleave, Shadowflame, Vengeance, Explorer Imp.

---

## Druid

**Page counts:** 14 new talents, 38 new or changed spells, 37 changed talents, 5 Classic spells/talents removed, 12 SoD leftovers.

### New abilities
- **Revive** (level 12, compared with an SoD version): out-of-combat resurrection with 70 health and 135 mana at rank 1. Costs 75% of base mana and has 5 ranks. Usable in caster or Moonkin form. [both]
- **Lacerate** (level 42, was an SoD rune): a stacking bear bleed, 50 over 15 sec plus 10% weapon damage per stack (5 stacks), with high threat. Costs 15 rage. SoD did 100 over 15 sec and cost 10 rage. [both]

### Talent tree changes
New talents:
- **Genesis** (Balance tier 1, 5 ranks): +5% periodic damage and healing. [both]
- **Nature's Majesty** (Balance, replaces Natural Weapons): +4% spell and melee crit. [both]
- **Gift of the Earthmother** (Restoration tier 3): Rejuvenation, Swiftmend and Wild Growth have a 0.5 sec shorter global cooldown. [client]
- **Nature's Splendor** (Balance tier 3): longer Moonfire (+3 sec), Rejuvenation (+3 sec), Regrowth (+6 sec) and Insect Swarm (+2 sec). [both]
- **Primal Bite** (Feral tier 4, active): 100% damage + 26 with high threat. [both]
- **Shifting Power** (Feral tier 4, active): converts mana into energy. Shapeshift cost reductions apply to it. [client]
- **Eclipse** (Balance tier 5): Wrath cuts your next 2 Starfires' cast time by 0.5 sec, storing up to 4 charges. [client]
- **Improved Shifting Power** (Feral tier 5): -8 sec cooldown. [both]
- **Living Spirit** (Restoration tier 5): +15% spirit. [both]
- **Natural Instinct** (Feral tier 5): melee ability crit damage +20%, and spell healing +25% of Intellect. [both]
- **Natural Reaction** (Feral tier 6, 5 ranks): +5% dodge, and gain 5 rage on dodge. [both]
- **Rend and Tear** (Feral tier 6, 5 ranks): +10% melee ability damage on bleeding targets. [both]
- **Berserk** (Feral tier 7, cat or bear): for 15 sec, Primal Bite hits 3 targets with no cooldown, combo point generators get +100% crit, and you're immune to fear. [client]
- **Wild Growth** (Restoration tier 7, was SoD): party HoT of 280 over 7 sec at rank 1 (front-loaded), 550 mana. Ranks go up to 679. [both]

Reworked talents:
- **Ferocity**: also covers Primal Bite. [data]
- **Furor**: bear still gets 10 rage. Entering cat form now restores the energy you had last time, plus 10 per sec spent out of bear form, up to 100 (was a flat 40). [client]
- **Heart of the Wild**: +10% Intellect and +10% cat strength (was 20% for both). Bear stamina stays +20%. Moved to tier 1. [both]
- **Improved Wrath**: also -50% Wrath cost. [data]
- **Nature's Focus**: covers all Arcane and Nature spells. [data]
- **Brutal Impact**: also -30 sec Bash cooldown. [data]
- **Feral Instinct**: 3 ranks, +30% Swipe damage (was +15% bear threat), and prowl as if 3 levels higher. [data]
- **Feral Swiftness** (renamed from Feline Swiftness): the cat speed bonus is no longer outdoors-only. [data]
- **Improved Moonfire**: 2 ranks. **Natural Shapeshifter**: moved to Restoration. [both]
- **Moonglow**: -25% cost on all damaging spells (was -9% on six spells). [data]
- **Naturalist**: also +5% all damage. [data]
- **Nature's Reach**: all offensive Balance spells, and also +4% hit. [data]
- **Subtlety**: 3 ranks, -30% threat on Nature and Arcane spells. [data]
- **Thick Hide**: 3 ranks, gives base armor per level in any form (was +10% item armor). [client]
- **Feral Charge**: requires bear form, and the tooltip adds a "Leap behind an enemy" line. [client]
- **Improved Entangling Roots**: +75% Roots damage, and the target can take 75% more damage before breaking free (was pushback protection). [client]
- **Reflection**: 50% regen while casting (was 15%). [data]
- **Savage Fury**: +10% (was +20%), now also covering Shred. [data]
- **Sharpened Claws**: 2 ranks (same +6%). [data]
- **Shredding Attacks** (renamed from Improved Shred): 3 ranks, Shred -18 energy, and Lacerate -3 rage. [data]
- **Blood Frenzy**: also gives cat-form crits from combo point generators an extra combo point. [client]
- **Swiftmend**: heals for the full periodic amount of the HoT it consumes. [data]
- **Vengeance** and **Moonfury**: now apply to all Arcane and Nature spells. [data]
- **Improved Tranquility**: also -60% cooldown. [data]
- **Leader of the Pack**: now covers the raid. [data]
- **Nature's Grace**: non-periodic spell crits give +10% casting speed and a 10% shorter global cooldown for 3 sec (was -0.5 sec on the next cast). [client]
- **Overgrowth** (renamed from Improved Nature's Grasp): 2 ranks, +2 max targets for Entangling Roots. [client]
- **Moonkin Form**: Omen of Clarity procs 100% more often in it, the aura reaches 45 yd and the raid, and you can't heal in it. It no longer limits you to Balance spells. [both]
- **Insect Swarm**: moved to Balance tier 4, about 27-43% less damage (rank 5: 186 instead of 324), and usable in Moonkin form. [both]
- Moves or wording only: Gift of Nature, Improved Starfire, Predatory Strikes, Improved Rejuvenation. [both]
- Talents that became trainer spells: **Nature's Grasp** (trainer level 10) and **Omen of Clarity** (trainer level 20). [both]

Removed talents: Faerie Fire (Feral) (normal Faerie Fire now works in cat and bear form), Feral Aggression, Improved Enrage, Improved Mark of the Wild, Improved Thorns. [data]

### Notable changed spells
Balance damage:
- **Wrath** ranks 2-8: about 25-64% less (rank 8: 86-96 instead of 236-264), and about 33% cheaper. [data]
- **Moonfire** ranks 2-10: about 25-40% less (rank 10: 124-146 + 240 over 12 sec instead of 189-221 + 384). [data]
- **Starfire** ranks 1-7: about 18-35% less (rank 7: 350-412 instead of 496-584). [data]
- **Entangling Roots** ranks 2-6: about 10-20% less damage. No longer outdoors-only, but limited to one rooted target at a time. [both]
- **Hurricane**: no cooldown (was 1 min). The client has no damage number (server-calculated; tooltip 132 per sec instead of 134). Attack-speed slow is 20% (was 25%). [both]

Healing:
- **Healing Touch** ranks 3-11: about 6-14% less (rank 11: 2139-2525 instead of 2267-2677) and about 3-5% more expensive. [data]
- **Rejuvenation** ranks 2-11: about 13-30% less (rank 11: 776 instead of 888). [data]
- **Regrowth** ranks 1-9: about 4-10% less, but about 40% cheaper (rank 9: 525 mana instead of 880). [data]
- **Tranquility** ranks 1-4: about 3-7% less, now 20 yd party only. [data]

Feral:
- **Shred** ranks 1-5: 155% weapon damage (was 225%). Rank 1 learned at level 20 (was 22). [data]
- **Claw** ranks 1-5: 110% weapon damage plus the same flat bonus (was 100%). [data]
- **Rake** ranks 1-4: about 5-20% more. [data]
- **Rip** ranks 4-6: about 9-12% less (rank 6 at 5 points: 855 instead of 942). [data]
- **Cat Form**: +12 AP plus Agility (was +40). Cat Form, Claw, Prowl and Rip rank 1 now come from the quest "The Great Cat Spirit" instead of the trainer. [both]
- **Bear Form**: the tooltip says +180 health (was +20), with the same data. [both]
- **Demoralizing Roar** ranks 1-5: about 50% stronger (rank 5: -193 AP instead of -130). [data]
- **Growl**: cooldown 8 sec (was 10). [data]
- **Enrage**: now 10 rage instantly plus 20 over 10 sec. [data]
- **Frenzied Regeneration**: 1 rank, each rage point becomes 1% of health (was 10 health). [data]
- **Ferocious Bite**: wording only. [data]

Utility and buffs:
- **Mark of the Wild** ranks 1-7 and **Gift of the Wild**: about 35% stronger (rank 7: 385 armor, +16 stats and +27 resistances, instead of 285/12/20). Mark lasts 1 hr (was 30 min). Gift covers the raid. Both usable in Moonkin form. [data]
- **Omen of Clarity**: now a passive trainer spell (no cost, no duration) that procs from spells and attacks. Wrath and free abilities don't consume it. [client]
- **Nature's Grasp**: trainer level 10, any form, and always roots the next melee attacker. [data]
- **Barkskin**: no mana cost, any form, and no longer slows your casts or melee. [data]
- **Faerie Fire**: usable in cat, bear and dire bear form. [data]
- Usable in Moonkin form now: Cure Poison, Abolish Poison, Remove Curse (now also removes Banes), Rebirth, Innervate, Mark of the Wild, Gift of the Wild, Revive. [data]

### Mechanic and system changes
- Shapeshift flexibility: many utility spells are castable in Moonkin or feral forms, and Moonkin no longer limits you to Balance spells. [data]
- New feral resources: Shifting Power (mana to energy) and the Furor energy memory. [client]
- Cat form becomes a quest unlock ("The Great Cat Spirit"). [both]
- Omen of Clarity becomes baseline and passive. [client]
- SoD leftovers (Speculation: probably unused): Deeper Wilds, Efflorescence, Enhanced Restoration, Lifebloom, Living Seed, Nourish, Skull Bash, Starfall, Starsurge, Sunfire, Survival Instincts, Tiger's Fury.

---

## Cross-class themes

- **Caster spell damage cut hard, top ranks hardest.** Shadow Bolt is down about 47%, Corruption about 47%, Lightning Bolt about 56%, Chain Lightning about 77%, Wrath about 64%, Smite about 57%, Fireball about 29% and Starfire about 29%. Healing is cut far less (mostly 5-15%, with Prayer of Healing about 40% as the exception). Low ranks are often untouched or slightly buffed, because the low-level coefficient penalty was removed. [data]
- **Spell hit replaces "reduce the target's chance to resist".** Arcane Focus, Elemental Precision, Shadow Focus and Suppression now give spell hit, and new hit talents arrive (Holy Precision, Divine Precision, Tidal Focus, Nature's Reach). Several melee talents now say "all attacks" instead of "melee weapons" (Precision, Lethal Attacks, Thundering Strikes, Malice includes poisons). This points to a unified hit and crit model. [both]
- **Mana regen while casting is everywhere at 50%.** Meditation, Arcane Meditation, Reflection and Mage Armor go from 15-30% to 50%, and Paladin (Reverence), Shaman (Mindfulness), Hunter (Bestial Discipline and others) and Warlock (Soul Harvest) gain new sources. [data]
- **Intellect converts into AP or spell power.** Careful Aim, Mental Dexterity, Champion of the Light, Mental Quickness, Natural Instinct (healing), Arcane Blast stacks, and Demonic Knowledge (spell power by level). [both]
- **"Max mana +X%" talents become "Intellect +X%".** Mental Strength, Ancestral Knowledge, Arcane Mind, Heart of the Wild. [data]
- **Buffs last 1 hour and reach the whole raid.** Blessings (all 1 hr), Power Word: Fortitude, Divine Spirit, Arcane Intellect, Mark of the Wild. Party buffs and auras become party-and-raid: Prayer of Fortitude/Spirit/Shadow Protection, Arcane Brilliance, Gift of the Wild, Battle Shout, Trueshot Aura, Paladin resistance auras, Aspect of the Wild, Leader of the Pack, Moonkin aura and totems. Several melee AP buffs are weaker in exchange: Battle Shout about -40%, Blessing of Might about -28%, Aspect of the Hawk about -50%, Strength of Earth about -30%. [data]
- **Totems and imbues last longer.** Totems last 5 min with a 30 yd radius. Shaman weapon imbues last 60 min. [data]
- **Resist-chance talents become duration reduction.** Iron Will, Unyielding Faith, Surefooted, Silent Resolve. [data]
- **New debuff categories and named mechanics.** Banes (Warlock, dispellable by Remove Curse/Lesser Curse), Forbearance (Paladin), Paladin judgements that last 40 sec and don't consume the seal, and a weapon-imbue vs totem anti-stacking rule. [client]
- **"Non-trivial enemy" replaces "yields experience or honor"** in kill-trigger effects: Victory Rush, Remorseless Attacks, Spirit Tap, Rapid Killing, Drain Soul, Shadowburn, Soul Harvest. [client]
- **SoD runes become baseline spells or talents.** Victory Rush, Hammer of the Righteous, Lacerate (Hunter and Druid), Shadow Word: Death, Fire Nova, Totemic Projection, Frostfire Bolt, Portal of Summoning and Revive become spells. Mutilate, Binding Heal, Penance, Prayer of Mending, Lava Burst, Riptide, Arcane Blast, Ice Lance, Incinerate and Wild Growth become talents. Almost all are weaker than their SoD versions and cost more. [both]
- **Several Classic talents become trainer spells.** Blessing of Kings, Consecration, Aimed Shot, Divine Spirit, Lightwell, Nature's Grasp, Omen of Clarity, and Tactical Mastery as a baseline ability. [both]
- **Weapon specializations are merged.** Warrior Weaponmaster and Rogue Hack and Slash give one bonus that depends on weapon type, replacing the separate Axe, Mace, Sword, Polearm, Fist and Dagger talents. [both]
- **Talent trees are compressed.** Talents move to lower tiers, the last tier (7) is reached at level 40, and many 5-rank talents become 2-3 ranks with similar totals. Each class also gets 5-18 new talents. [both]
- **More +100% crit damage effects.** Shadowform, Arcane Mind, Pandemic, Elemental Fury (now 5 ranks), and Vengeance and Moonfury widened to whole schools. [data]
- **Reagents and conjured items.** Warlock stones are merged into one ranked spell each, and Firestone and Spellstone become weapon enchants. Portal of Summoning costs 5 shards. Fire Nova no longer needs a Fire Totem item. [both]
- **Class mounts are removed from class ability lists** (Paladin Warhorse and Charger, Warlock Felsteed and Dreadsteed). Their spells are still in the files. [data]
- **Removed spells.** Detect Magic, Curse of Shadow, Greater Blessing of Sanctuary, Fire Nova Totem, Tranquil Air Totem. Weapon skills change: Shaman loses Dual Wield, Rogue gains One-Handed Axes. [both]
- **Some AoE damage is calculated by the server.** Consecration, Volley, Blizzard, Rain of Fire and Hurricane have no damage number in the Forever client, and Volley and Hurricane lose their cooldowns. Port these by formula rather than by DBC value. [both]
- **Shorter cooldowns on big defensives.** Shield Wall and Retaliation drop to 15 min, Lay on Hands to 20 min, Devouring Plague to 24 sec. Hunter traps go the other way, up to 30 sec, but can be placed in combat. [data]
