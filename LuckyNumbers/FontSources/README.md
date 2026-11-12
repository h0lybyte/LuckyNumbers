# Font sources

The fonts as published, in full (outside Assets, so Unity never imports or ships them).
The game uses subsets of them: `python3 tools/ln.py fonts` (needs `pip install fonttools`) writes each
`FontSources/<family>/<name>.ttf` to `Assets/_Project/Art/Fonts/<family>/<name>.ttf`, keeping only the
characters in `FONT_UNICODES` (tools/ln.py): Latin, punctuation, arrows, shapes. Widen that list and rerun
before content uses another script; missing characters draw as boxes.

- Exo2: Exo 2 by the Exo 2 Project Authors, SIL Open Font License 1.1 (Exo2/OFL.txt, no Reserved Font Name).
