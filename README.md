<h1>Terraria "Better Aim" Gameplay Enhancement Mod</h1>
A gameplay modification for Terraria that provides advanced, physics-based aiming assistance for over 80 unique weapons.

![aimlock-optimized](https://github.com/user-attachments/assets/8f3232e1-852a-4542-bc85-35f3292e67ae)

<h1>About The Project</h1>
This is a mod for the game Terraria that adds a suite of advanced aiming tools for players. As a fan of the game, I was interested in the unique physics of its different weapons and wanted to see if I could apply my knowledge of kinematics to model and predict their trajectories.

This mod was built from scratch in C# and uses the Harmony library to safely patch the game at runtime, ensuring compatibility and stability.

<h1>Key Features</h1>

- Predictive Aiming: The core of the mod is a system that applies kinematic principles to accurately predict the trajectory of over 500 unique in-game weapons. It algorithmically accounts for dynamic variables like gravity, acceleration, and drag.

- Multiple Targeting Algorithms: Includes several different aiming modes to suit any situation, such as "Closest to Cursor," "Lowest Health," and a persistent "Aimlock" that stays on a single enemy.

- Automated Triggerbot: An optional feature that automates weapon usage when a clear line of sight to the locked target is established, based on the game's collision detection system.

<h1>Built With</h1>

- C#

- Harmony (Patching Library)

- Microsoft XNA Framework
