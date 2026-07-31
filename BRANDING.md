# Oh My Skill branding

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="src/OhMySkill/Assets/Branding/p3.png">
    <source media="(prefers-color-scheme: light)" srcset="src/OhMySkill/Assets/Branding/p2.png">
    <img src="src/OhMySkill/Assets/Branding/p2.png" width="128" alt="Oh My Skill logo">
  </picture>
</p>

## Asset map

| Asset | Intended surface |
| --- | --- |
| `p2.png` | Cyan mark on a light background |
| `p3.png` | Cyan mark on a dark background |
| `p1.png` | White monochrome fallback on dark backgrounds |
| `p4.png` | Black monochrome fallback on light backgrounds |
| `OhMySkill.ico` | Multi-resolution Windows application and EXE icon |

The WPF application uses the cyan mark in its header, home, recording, and
review states. GitHub documentation uses a light/dark `<picture>` so the mark
remains legible in both themes. All logo uses include accessible text such as
`Oh My Skill logo`.

## Rights

The Oh My Skill mark and the supplied `p1.png`, `p2.png`, `p3.png`, `p4.png`,
and `OhMySkill.ico` files are original artwork created and owned by the project
maintainer. Permission is granted to use, modify, and redistribute these assets
with the project under the repository's MIT License. Use of the Oh My Skill
name or mark must not imply endorsement of a modified distribution.
