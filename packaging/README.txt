RIOTOFFLINEMODEENHANCED
=======================

RiotOfflineModeEnhanced lets you appear offline (or on mobile) in Riot chat
while you play VALORANT, League of Legends and other Riot games. You can still
pick a few friends who see your real status.


GETTING STARTED
---------------

1. Extract this whole folder somewhere, such as your Desktop or Documents.
   Don't run it from inside the zip file. Keep RiotOfflineModeEnhanced.exe and
   RiotOfflineModeEnhanced.exe.config together in the same folder.

2. Close the Riot Client if it's open.

3. Double-click RiotOfflineModeEnhanced.exe. Pick the game you want to launch.
   RiotOfflineModeEnhanced starts the Riot Client for you.

   Always start your game through RiotOfflineModeEnhanced.exe instead of the
   normal Riot shortcut. That's what lets it hide your status.

Windows may show "Windows protected your PC" the first time, because the app
isn't signed. Click "More info", then "Run anyway".

If you downloaded the zip, you can also right-click it, choose Properties,
tick "Unblock" and click OK before extracting.


USING IT
--------

- RiotOfflineModeEnhanced runs in the system tray (bottom-right of the
  taskbar, under the ^ arrow). Left-click the icon to open the
  RiotOfflineModeEnhanced window. Right-click it for the menu.

- In the window you can:
    * choose how you appear: Online, Offline or Mobile
    * turn masking off completely (everyone sees you online)
    * tick friends who should still see your real status while you appear
      offline or on mobile

- The Store tab (VALORANT) shows today's four skins and the Night Market
  when it's open, with prices, discounts, reset timers and your wallet.

- The Agent select tab (VALORANT) shows your team while you pick agents:
  names, agents, level, current rank and RR, this act's wins and losses and
  peak rank. Hidden names stay hidden unless they're on your friends list
  (their stats still show), and players who hide their act rank keep it
  hidden.

- A friend called "RiotOfflineModeEnhanced" appears at the top of your friends
  list. It's not a real player. Message it to control RiotOfflineModeEnhanced
  from chat:
    online, offline, mobile    change how you appear
    enable, disable            turn masking on or off
    status, visible            show your current status and who can see you
    help                       list the commands

- Closing the RiotOfflineModeEnhanced window doesn't stop
  RiotOfflineModeEnhanced. It keeps running in the tray.

- To quit, right-click the tray icon and choose Quit. This also closes the
  Riot Client and your game.


SETTINGS FILE
-------------

The first time it runs, RiotOfflineModeEnhanced creates
RiotOfflineModeEnhanced-settings.json next to RiotOfflineModeEnhanced.exe. You
can edit it in Notepad (window > "Edit settings file") to change the fake
friend's name, player card and level. Most changes apply as soon as you save
the file.


GOOD TO KNOW
------------

- RiotOfflineModeEnhanced changes your status in Riot's chat. It doesn't
  touch the game itself. The Store and Agent select tabs only read data from
  Riot's VALORANT web services using your Riot Client session, and only while
  that tab is open. It is still an unofficial third-party tool, so use it at
  your own risk.

- If the Store or Agent select tab can't find your region, launch VALORANT
  once with the app running. It remembers your region after that.

- Friends you tick only see you if they're on your friends list. If a name
  shows "Not on friends list", check the spelling and the #tag.

- Requirements: Windows 10 or 11. The .NET Framework it needs is already
  part of Windows.

- RiotOfflineModeEnhanced also keeps a few small files, like your last status
  and a debug log, in %APPDATA%\Deceive (the same folder the original Deceive
  uses).


CREDITS AND LICENSE
-------------------

RiotOfflineModeEnhanced is not affiliated with, endorsed by, or sponsored by
Riot Games. Riot Games, VALORANT and League of Legends are trademarks of Riot
Games, Inc.

RiotOfflineModeEnhanced is a modified version of Deceive by molenzwiebel
(https://github.com/molenzwiebel/Deceive), and like Deceive it is free
software under the GNU General Public License v3.0. See LICENSE.txt.

The complete source code, the list of changes from Deceive, and every
release are on GitHub:

  {{REPO_URL}}

Releases there are built automatically by GitHub Actions from that source.
Please don't report problems with RiotOfflineModeEnhanced to the Deceive
author.
