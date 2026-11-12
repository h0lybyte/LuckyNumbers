# LuckyNumbers

Unity 6000.6.8f1 (URP) Web game: WebGPU + WebGL 2 fallback, multithreaded Burst jobs, released to itch.io (`kbve/lucky-numbers`).

```
python3 tools/ln.py doctor      # check setup
python3 tools/ln.py build-web   # editor closed
python3 tools/ln.py serve --open
moon run game:worktrees         # which git worktrees are done (merged, clean, idle); game:worktrees-prune removes them
```

Open the `LuckyNumbers/` subfolder in Unity Hub. Release: new `## x.y.z` at the top of `CHANGELOG.md`, merge to `main`.

Docs: [docs/](docs/README.md)

<!-- credits:begin (generated from content/credits.txtpb by tools/ln.py gen; edit that file) -->
## Credits

- Museum freighter (the cargo ship that crashes): [Deep Space Science Survey Vessel (PSX lo-fi)](https://sketchfab.com/3d-models/deep-space-science-survey-vessel-psx-lo-fi-925a09ef43fe4675acd0a935d61dcd43) by Vaportrash ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
- Fighter ship and skins: [FREE Low Poly Space ship pack (fighter1)](https://retrovandal.itch.io/low-poly-space-ship-pack) by RetroVandal (formerly Psionic Games) (Free for freeware, shareware and commercial games)
- Water Amulet (Zhang Heng's relic): [Amulet](https://blendswap.com/blend/15782) by Mierenttt ([CC BY 3.0](https://creativecommons.org/licenses/by/3.0/))
- Roman Vase (Pliny the Elder's relic): [ROMAN VASE](https://sketchfab.com/3d-models/roman-vase-e63cb75682d144cab9e7f88343efff2c) by RenderX ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
- Aztec Block (Nezahualcoyotl's relic): [aztec block a](https://sketchfab.com/3d-models/aztec-block-a-415b2d7d208a4e73a63ba1b3bd682269) by RenderX ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
- Aberration (the limping creature you fight on planets: rig and 17 animations): [Aberration - PSX Horror Creature](https://rafaelmarrtins.itch.io/aberration-psx-horror-creature) by Rafael Martins (Blackwood Asylum: The Rake) (Use in games allowed; the asset files may not be redistributed)
- Rattleframe (the skeleton you fight on planets: mesh, rig and skin): [PSX Skeleton Character](https://vinrax.itch.io/psx-skeleton-character) by Vinrax (Free to use in projects with credit)
- Rattleframe's animations (idle, shamble, jog, claw swipe, flinch, death), retargeted onto the skeleton: [Universal Animation Library and Universal Animation Library 2](https://quaternius.com) by Quaternius ([CC0, public domain](https://creativecommons.org/publicdomain/zero/1.0/))
- Plague Arcanist (the beaked caster you fight on planets: mesh, rig, skin and animations): [Plague Arcanist - Retro PS1 Low Poly Magic Character](https://imaginais.itch.io/plague-arcanist-retro-ps1-low-poly-magic-character) by imaginais (Free to use, commercial projects included (the author's reply on the asset page); credit appreciated)
- Space station corridors and rooms (cream panels, leaning walls, ribs, portholes, doorways, deck): [PSX Starship Kit Free](https://heyheythere.itch.io/psx-starship-kit-free) by heyheythere ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
- Space station props (imported for later: cryo pods, crates, lab bench, chairs) and early room walls: [PSX Sci-Fi Station Kit Free](https://heyheythere.itch.io/psx-sci-fi-station-kit-free) by heyheythere ([CC BY 4.0](https://creativecommons.org/licenses/by/4.0/))
- First-person arms (rig, 13 animations, skin and glove textures): [PSX First Person Arms](https://drillimpact.itch.io/psx-first-person-arms-free) by DrillImpact (CC0, public domain)
- Futuristic weapons: 9 guns (rifles, snipers, carbine, heavy cannon), 2 melee weapons (hammer, knife) and gun mods (scope, sight, grip, box): [Free Futuristic Weapon Assets](https://niko-3d-models.itch.io/free-futuristic-weapon-assets) by Niko-3d-models (Free with credit)
- Dwarven hold surfaces (stone, slate, wood, metal, doors, windows, tapestries, lanterns), for the dwarven worlds' structures: [Delven Textures](https://strideh.itch.io/delven) by Bradley D. (Strideh) (Free for free and commercial games, with attribution; not for resale or AI training)
- Planet shader look: [Simple 2D Planet shader](https://www.shadertoy.com/view/Wcf3W7) by Ikkemcwood (Shadertoy)
- Seeded nebula sky (layered gas, dark dust, emission knots): [Nebula Background Shadertoy Study (inspiration)](https://github.com/guinetik) by guinetik
- Forming-system dust disk: [Protoplanetary disk (inspiration)](https://www.shadertoy.com/view/MdtGRl) by Duke (Shadertoy)
- Noise and fBm techniques: [The Book of Shaders](https://thebookofshaders.com) by Patricio Gonzalez Vivo and Jen Lowe
- 3D simplex noise: [webgl-noise](https://github.com/ashima/webgl-noise) by Ian McEwan, Stefan Gustavson and Ashima Arts (MIT)
- UI font: [Exo 2](https://github.com/googlefonts/Exo-2.0) by Natanael Gama / The Exo 2 Project Authors (SIL Open Font License 1.1)
- HUD readout font (web overlay): [Share Tech Mono](https://fonts.google.com/specimen/Share+Tech+Mono) by Carrois Apostrophe (SIL Open Font License 1.1)
- Libraries: [UniTask](https://github.com/Cysharp/UniTask), [OpenIK](https://github.com/WouterApts/openik-unity), [R3](https://github.com/Cysharp/R3), [ObservableCollections](https://github.com/Cysharp/ObservableCollections), [MessagePipe](https://github.com/Cysharp/MessagePipe), [VContainer](https://github.com/hadashiA/VContainer), [NuGetForUnity](https://github.com/GlitchEnzo/NuGetForUnity) (MIT)

Details: [CREDITS.md](CREDITS.md) (also in the game: title screen, Credits).
<!-- credits:end -->
