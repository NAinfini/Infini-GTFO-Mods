# Third-party source notices

2.5.2 independently implements the visible behavior inspected in Localia ResourceHelper package 3.0.1 (assembly reports 3.0.0): Res_Manager, Res_Moniter, MarkUI_Manager and ADS/PING patches. ResourceHelper code, textures and DLL are not redistributed. Native lifecycle and feature differences are recorded in HUD-BEHAVIOR.md.

2.4.3 continues independent implementation using the native lifecycle observed in ItemMarker 1.1.0, DropItemPlus 1.0.1, HUDInfoPlus 1.0.1, StatDisplay 1.1.8 and NoInterruptions 0.1.12. Fixed repository URLs, commits and release-package hashes are recorded in UPSTREAM-ACCEPTANCE.md. The inspected repositories did not contain license files; no upstream source file, asset or DLL is copied into this package. Native game assemblies are referenced at build/runtime, not redistributed.

2.4.2 also checks ItemMarker's container-open and pickup-state behavior, DropItemPlus 1.0.1 (https://github.com/Mamizu1028/GTFO_DropItem) native Interact_Timed slot lifecycle and stock compartment measurements, cached ResourceHelper 3.0.1 and PacksHelper 3.1.4, and Dinorush StatDisplay accuracy eligibility/text anchoring. The production implementation is written in Infini source around native game APIs. No upstream source file or DLL is bundled. Upstream feature parity and Unity acceptance are not inferred from this comparison.

2.4.1 dynamic registration references ItemMarker's native-event behavior: https://github.com/Mamizu1028/GTFO_ItemMarker, commit 5b147221326d48402a874e2f625c63f6ef15b871, Features/ItemMarker.cs and Handlers/ItemMarkerBase.cs. No license file was present in the inspected checkout. This change is an independent implementation around the game's native APIs; no ItemMarker DLL or source file is bundled.

Version 2.4.0 removes the WeaponDescriptions implementation and its adapted files at the user's request. Chat and weapon descriptions are supplied by the separately installed Archive. Combat statistics are independently written source referencing the behavior of Dinorush StatDisplay 1.1.8 (https://github.com/Dinorush/StatDisplay); no StatDisplay assembly or upstream source files are bundled. Terminal interaction behavior was compared with NoInterruptions; it is implemented in Infini source around native game APIs.

The following historical attribution is retained for earlier 2.3.x source/package history: WeaponDescriptionBuilder.cs, ArchetypeUtil.cs, SleepersDatas.cs and Language/* were adapted from DescriptiveWeaponStatShower (Dacre, Amorously and contributors), https://github.com/Amorously/GTFO-WeaponStatShower, commit 929ee3bec2487902c24ea1619a1e467f17b0bf97. These files are not compiled into 2.4.0.

## MIT License

Copyright (c) 2023 Dacre

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
