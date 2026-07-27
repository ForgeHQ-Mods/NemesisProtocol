Changelog
Version 1.0.1

Added:

Added unique, persistent Messenger identities for individual Nemeses.
Rivalry messages now come directly from the Nemesis rather than from Nemesis Network.
Added persistent Russian-style first and last names for Nemeses.
Added a randomized pool of Tarkov-appropriate Russian names.
Added Nemesis Network as a separate Messenger contact for rivalry records and commands.
Added the following Nemesis Network commands:

help
status
rivals
history
rival 1
rival <name>
compat
compatibility

Added detailed rival statistics and encounter information.
Added live APBS and ORBIT compatibility reporting.
Added ORBIT installation-path reporting.
Added automatic data migration for existing Nemesis profiles.
Added persistent Messenger dialogue IDs for each rival.
Added saved original bot nicknames through the internal sourceBotName field.

Changed:

Creation, repeat-kill, escape, and defeat messages are now sent by the individual rival.
Nemesis Network now functions as the rivalry database and command interface.
Existing Nemeses are automatically assigned persistent Russian-style identities.
Rival detail commands now check compatibility status live instead of displaying outdated saved information.
ORBIT detection now searches both the server directory and its parent installation directory.
Improved support for installations where the SPT server and BepInEx folders are stored in different locations.
Updated the default configuration:
42% appearance chance.
No required cooldown raid between appearances.
Maximum of 16 bonus levels.
Two levels gained per player kill.
One level gained per successful escape.
Eight percent additional health per rank.
Matching-faction replacement enabled by default.
Nemeses remain standard USEC or BEAR PMCs so installed AI frameworks retain behavioral control.

Fixed:

Fixed Nemesis Network not appearing as a usable Messenger contact.
Fixed Nemesis Network commands not being routed to the chatbot.
Fixed the first rivalry message failing to reveal the Nemesis’s identity.
Fixed individual rival messages appearing under the Nemesis Network name and level.
Fixed false-negative ORBIT detection when ORBIT was installed under a parent-level BepInEx/plugins folder.
Fixed rival 1 showing SPT/Installed AI mods after ORBIT had already been detected.
Fixed stale build files occasionally being included in newly compiled packages.
Fixed build verification incorrectly reporting that the Nemesis chatbot class was missing.
Improved startup diagnostics for Messenger and compatibility registration.

Profile Compatibility:

Existing Nemesis progression, faction, statistics, encounter history, Messenger identity, and active-rival status are preserved.
Existing profiles are migrated automatically.
A profile reset is not required.
