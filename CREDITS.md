# Credits

<!-- Generated from content/credits.txtpb by tools/ln.py gen; edit that file. -->

Lucky Numbers is made with the work of these people. Thank you.

## Models

- **Museum freighter (the cargo ship that crashes)**: "Deep Space Science Survey Vessel (PSX lo-fi)" by **Vaportrash** ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
  https://sketchfab.com/3d-models/deep-space-science-survey-vessel-psx-lo-fi-925a09ef43fe4675acd0a935d61dcd43
  Changes: converted from glTF to FBX split into one part per material (tools/blender/gltf_to_fbx_by_material.py), textures downsized to 512 px, and a convex collision mesh generated from it (tools/collision/decompose.py).
  (`LuckyNumbers/Assets/_Project/Art/Models/CargoShip/`)
- **Fighter ship and skins**: "FREE Low Poly Space ship pack (fighter1)" by **RetroVandal (formerly Psionic Games)** (Free for freeware, shareware and commercial games)
  https://retrovandal.itch.io/low-poly-space-ship-pack
  From a 2005 3D Game Resources skinning competition. Skins (named after their authors): IdolKnight (1st), Krulspeld (2nd), Kaos Kiwi (3rd), Lordshadow (4th), Wilko, Cinfa, Crono782, Cubik, Freelancer, Jodomatis, NMT3000, Psychic Parrot, Robin, Shadee, Snaz, Thor and Psionic.
  (`LuckyNumbers/Assets/_Project/Art/Models/Fighter/README-psionic.txt`)
- **Water Amulet (Zhang Heng's relic)**: "Amulet" by **Mierenttt** ([CC BY 3.0](https://creativecommons.org/licenses/by/3.0/))
  https://blendswap.com/blend/15782
  Changes: exported low-poly (chain subdivision off), modifiers baked, recoloured with the relic shader (tools/blender/export_relic.py).
  (`LuckyNumbers/Assets/_Project/Art/Models/Relics/LICENSE-blendswap-80554.html`)
- **Space station corridors and rooms (cream panels, leaning walls, ribs, portholes, doorways, deck)**: "PSX Starship Kit Free" by **heyheythere** ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
  https://heyheythere.itch.io/psx-starship-kit-free
  Changes: an alpha channel added to atlas.png as a light mask and the atlas packed with the station kit's into one (Art/Models/CombinedKit, tools/kitmask.py), RGB unchanged; room walls derived from the corridor walls with their lean cut back, corner fillers, window panes drawn as clear glass and door leaves slid in the shader, at build time.
  (`LuckyNumbers/Assets/_Project/Art/Models/StarshipKit/LICENSE.txt`)
- **Space station props (imported for later: cryo pods, crates, lab bench, chairs) and early room walls**: "PSX Sci-Fi Station Kit Free" by **heyheythere** ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
  https://heyheythere.itch.io/psx-sci-fi-station-kit-free
  Changes: an alpha channel added to atlas.png as a light mask and the atlas packed with the starship kit's into one (Art/Models/CombinedKit, tools/kitmask.py), RGB unchanged.
  (`LuckyNumbers/Assets/_Project/Art/Models/StationKit/LICENSE.txt`)
- **First-person arms (rig, 13 animations, skin and glove textures)**: "PSX First Person Arms" by **DrillImpact** (CC0, public domain)
  https://drillimpact.itch.io/psx-first-person-arms-free
- **Futuristic weapons: 9 guns (rifles, snipers, carbine, heavy cannon), 2 melee weapons (hammer, knife) and gun mods (scope, sight, grip, box)**: "Free Futuristic Weapon Assets" by **Niko-3d-models** (Free with credit)
  https://niko-3d-models.itch.io/free-futuristic-weapon-assets
  Changes: split into one FBX per weapon, modifiers baked, centred and scaled to metres (tools/blender/export_weapons.py).
  (`LuckyNumbers/Assets/_Project/Art/Models/Weapons/LICENSE.txt`)

## Shaders

- **Planet shader look**: "Simple 2D Planet shader" by **Ikkemcwood** (Shadertoy)
  https://www.shadertoy.com/view/Wcf3W7
  Adapted to 3D spheres.
- **Seeded nebula sky (layered gas, dark dust, emission knots)**: "Nebula Background Shadertoy Study (inspiration)" by **guinetik**
  https://github.com/guinetik
  Written fresh, no code copied.
- **Forming-system dust disk**: "Protoplanetary disk (inspiration)" by **Duke** (Shadertoy)
  https://www.shadertoy.com/view/MdtGRl
  Written fresh, no code copied.
- **Noise and fBm techniques**: "The Book of Shaders" by **Patricio Gonzalez Vivo and Jen Lowe**
  https://thebookofshaders.com
- **3D simplex noise**: "webgl-noise" by **Ian McEwan, Stefan Gustavson and Ashima Arts** (MIT)
  https://github.com/ashima/webgl-noise

## Fonts

- **UI font**: "Exo 2" by **Natanael Gama / The Exo 2 Project Authors** (SIL Open Font License 1.1)
  https://github.com/googlefonts/Exo-2.0
  (`LuckyNumbers/Assets/_Project/Art/Fonts/Exo2/OFL.txt`)
- **HUD readout font (web overlay)**: "Share Tech Mono" by **Carrois Apostrophe** (SIL Open Font License 1.1)
  https://fonts.google.com/specimen/Share+Tech+Mono

## Libraries

- **Async/await**: "UniTask" by **Cysharp** (MIT)
  https://github.com/Cysharp/UniTask
- **Finger joint-limit math (hinge and ball-and-socket clamps), ported**: "OpenIK" by **Wouter Apts** (MIT)
  https://github.com/WouterApts/openik-unity
  (`LuckyNumbers/Assets/_Project/Scripts/Hands/OpenIK-LICENSE.md`)
- **Reactive state**: "R3" by **Cysharp** (MIT)
  https://github.com/Cysharp/R3
- **Observable collections**: "ObservableCollections" by **Cysharp** (MIT)
  https://github.com/Cysharp/ObservableCollections
- **Messaging**: "MessagePipe" by **Cysharp** (MIT)
  https://github.com/Cysharp/MessagePipe
- **Dependency injection**: "VContainer" by **hadashiA** (MIT)
  https://github.com/hadashiA/VContainer
- **NuGet packages in Unity**: "NuGetForUnity" by **GlitchEnzo** (MIT)
  https://github.com/GlitchEnzo/NuGetForUnity
