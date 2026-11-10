# Credits

## Shaders

- Planet shader look: "Simple 2D Planet shader" by **Ikkemcwood** on Shadertoy, https://www.shadertoy.com/view/Wcf3W7 (adapted to 3D spheres)
- Seeded nebula sky (layered gas, dark dust, emission knots), inspired by "Nebula Background Shadertoy Study" by **guinetik**, https://github.com/guinetik (written fresh, no code copied)
- Forming-system dust disk, inspired by "Protoplanetary disk" by **Duke** on Shadertoy, https://www.shadertoy.com/view/MdtGRl (written fresh, no code copied)
- Noise and fBm techniques: **The Book of Shaders** by Patricio Gonzalez Vivo and Jen Lowe, https://thebookofshaders.com
- 3D simplex noise: **webgl-noise** by Ian McEwan, Stefan Gustavson and Ashima Arts (MIT), https://github.com/ashima/webgl-noise

## Models

- Fighter ship "fighter1" by **RetroVandal** (formerly Psionic Games), "FREE Low Poly Space ship pack",
  https://retrovandal.itch.io/low-poly-space-ship-pack; free for freeware, shareware and commercial games;
  from a 2005 3D Game Resources skinning competition. Skins (`Art/Models/Fighter/Skins/`, named after their authors):
  IdolKnight (1st), Krulspeld (2nd), Kaos Kiwi (3rd), Lordshadow (4th), Wilko, Cinfa, Crono782, Cubik, Freelancer,
  Jodomatis, NMT3000, Psychic Parrot, Robin, Shadee, Snaz, Thor and Psionic.
  (readme: `LuckyNumbers/Assets/_Project/Art/Models/Fighter/README-psionic.txt`)

- First-person arms (rig, 13 animations, skin and glove textures): "PSX First Person Arms" by **DrillImpact**,
  https://drillimpact.itch.io/psx-first-person-arms-free (CC0 / public domain)

- Museum freighter (the cargo ship that crashes): "Deep Space Science Survey Vessel (PSX lo-fi)" by **Vaportrash**,
  https://sketchfab.com/3d-models/deep-space-science-survey-vessel-psx-lo-fi-925a09ef43fe4675acd0a935d61dcd43,
  licensed under Creative Commons Attribution (CC BY 4.0), https://creativecommons.org/licenses/by/4.0/.
  Changes: converted from glTF to FBX split into one part per material (`tools/blender/gltf_to_fbx_by_material.py`),
  textures downsized to 512 px, and a convex collision mesh generated from it (`tools/collision/decompose.py`).
  (`LuckyNumbers/Assets/_Project/Art/Models/CargoShip/`)

## Fonts

- **Exo 2** by Natanael Gama / The Exo 2 Project Authors (SIL Open Font License 1.1), https://github.com/googlefonts/Exo-2.0
  (licence: `LuckyNumbers/Assets/_Project/Art/Fonts/Exo2/OFL.txt`)

## Libraries

| Library | License | Source |
|---|---|---|
| UniTask | MIT | https://github.com/Cysharp/UniTask |
| R3 | MIT | https://github.com/Cysharp/R3 |
| ObservableCollections | MIT | https://github.com/Cysharp/ObservableCollections |
| MessagePipe | MIT | https://github.com/Cysharp/MessagePipe |
| VContainer | MIT | https://github.com/hadashiA/VContainer |
| NuGetForUnity | MIT | https://github.com/GlitchEnzo/NuGetForUnity |
