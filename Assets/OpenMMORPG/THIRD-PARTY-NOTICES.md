# Third-Party Notices

Open MMORPG is released under the MIT License; see [LICENSE](LICENSE).

This file lists third-party components distributed with the kit, as required for
Unity Asset Store submissions.

## Components with a licence file in this repository

| Component | Licence | Copyright | Licence file |
| --- | --- | --- | --- |
| `Core` | MIT | 2026 Ittipon Teerapruettikulchai | [`Core/LICENSE`](Core/LICENSE) |
| `Database` | MIT | 2023 Ittipon Teerapruettikulchai | [`Database/LICENSE`](Database/LICENSE) |
| `MMO` | MIT | 2018 Ittipon Teerapruettikulchai | [`MMO/LICENSE`](MMO/LICENSE) |
| `Server` | MIT | 2023 Ittipon Teerapruettikulchai | [`Server/LICENSE`](Server/LICENSE) |
| `SharedData` | MIT | 2023 Ittipon Teerapruettikulchai | [`SharedData/LICENSE`](SharedData/LICENSE) |
| `ThirdParty/AddressableAssetTools` | MIT | 2024 Ittipon Teerapruettikulchai | [`ThirdParty/AddressableAssetTools/LICENSE`](ThirdParty/AddressableAssetTools/LICENSE) |
| `ThirdParty/AudioManager` | MIT | 2017 Ittipon Teerapruettikulchai | [`ThirdParty/AudioManager/LICENSE`](ThirdParty/AudioManager/LICENSE) |
| `ThirdParty/CameraAndInput` | MIT | 2017 Ittipon Teerapruettikulchai | [`ThirdParty/CameraAndInput/LICENSE`](ThirdParty/CameraAndInput/LICENSE) |
| `ThirdParty/DevExtension` | MIT | 2018 Ittipon Teerapruettikulchai | [`ThirdParty/DevExtension/LICENSE`](ThirdParty/DevExtension/LICENSE) |
| `ThirdParty/GraphicSettings` | MIT | 2021 Ittipon Teerapruettikulchai | [`ThirdParty/GraphicSettings/LICENSE`](ThirdParty/GraphicSettings/LICENSE) |
| `ThirdParty/LiteNetLibManager` | MIT | 2017 Ittipon Teerapruettikulchai | [`ThirdParty/LiteNetLibManager/LICENSE`](ThirdParty/LiteNetLibManager/LICENSE) |
| `ThirdParty/SerializableCallback` | MIT | 2017 Thor Brigsted | [`ThirdParty/SerializableCallback/LICENSE.md`](ThirdParty/SerializableCallback/LICENSE.md) |
| `ThirdParty/SerializationSurrogates` | MIT | 2018 Ittipon Teerapruettikulchai | [`ThirdParty/SerializationSurrogates/LICENSE`](ThirdParty/SerializationSurrogates/LICENSE) |
| `ThirdParty/SpatialPartitioningSystems` | MIT | 2025 Ittipon Teerapruettikulchai | [`ThirdParty/SpatialPartitioningSystems/LICENSE`](ThirdParty/SpatialPartitioningSystems/LICENSE) |
| `ThirdParty/UnityEditorUtils` | MIT | 2018 Ittipon Teerapruettikulchai | [`ThirdParty/UnityEditorUtils/LICENSE`](ThirdParty/UnityEditorUtils/LICENSE) |
| `ThirdParty/UnityRestClient` | MIT | 2021 Ittipon Teerapruettikulchai | [`ThirdParty/UnityRestClient/LICENSE`](ThirdParty/UnityRestClient/LICENSE) |
| `ThirdParty/UpdateManager` | MIT | 2026 Ittipon Teerapruettikulchai | [`ThirdParty/UpdateManager/LICENSE`](ThirdParty/UpdateManager/LICENSE) |
| `ThirdParty/xNode` | MIT | 2017 Thor Brigsted | [`ThirdParty/xNode/LICENSE.md`](ThirdParty/xNode/LICENSE.md) |

## Components whose licence was confirmed upstream

These ship without their own licence file. The licence below was taken from the
project's own repository, or from the copyright recorded in the binary itself.

| Component | Path | Licence | Copyright | Source |
| --- | --- | --- | --- | --- |
| LiteNetLib | `ThirdParty/LiteNetLibManager/Plugins/LiteNetLib` | MIT | Copyright (c) 2025 Ruslan Pyrch | [licence](https://github.com/RevenantX/LiteNetLib/blob/master/LICENSE.txt) |
| UniTask | `ThirdParty/LiteNetLibManager/Plugins/UniTask` | MIT | Copyright (c) 2019 Yoshifumi Kawai / Cysharp, Inc. | [licence](https://github.com/Cysharp/UniTask/blob/master/LICENSE) |
| ZString | `ThirdParty/LiteNetLibManager/Plugins/ZString` | MIT | Copyright (c) 2020 Cysharp, Inc. | [licence](https://github.com/Cysharp/ZString/blob/master/LICENSE) |
| SerializeRegistrySourceGenerator | `ThirdParty/LiteNetLibManager/Scripts/SourceGenerators/SerializeRegistrySourceGenerator.dll` | MIT | Copyright (c) 2017 Ittipon Teerapruettikulchai | [licence](https://github.com/insthync/LiteNetLibManager/blob/main/LICENSE) |
| ConcurrentHashSet | `Core/Plugins/ConcurrentCollections.dll` | MIT | Copyright (c) 2019 Bar Arnon | [licence](https://github.com/i3arnon/ConcurrentHashSet/blob/main/LICENSE) |
| MySqlConnector | `MMO/Plugins/MySqlConnector.dll` | MIT | Copyright (c) 2016-2026 Bradley Grainger | [licence](https://github.com/mysql-net/MySqlConnector/blob/master/LICENSE) |
| Fleck | `ThirdParty/LiteNetLibManager/Plugins/Fleck.dll` | MIT | Copyright (c) 2010-2018 Jason Staten | [licence](https://github.com/statianzo/Fleck/blob/master/LICENSE) |
| System.Diagnostics.DiagnosticSource | `MMO/Plugins/System.Diagnostics.DiagnosticSource.dll` | MIT | Copyright (c) .NET Foundation and Contributors | [licence](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| System.Runtime.CompilerServices.Unsafe | `ThirdParty/LiteNetLibManager/Plugins/System.Runtime.CompilerServices.Unsafe.dll` | MIT | Copyright (c) .NET Foundation and Contributors | [licence](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| SQLite | `MMO/Plugins/SQLite_x64/sqlite3.dll, MMO/Plugins/SQLite_x86/sqlite3.dll` | Public domain | Dedicated to the public domain by its authors | [licence](https://www.sqlite.org/copyright.html) |
| Mono.Data.Sqlite | `MMO/Plugins/Mono.Data.Sqlite.dll` | Public domain | Declares "Public Domain"; originates from System.Data.SQLite and ships with Mono, whose class libraries are MIT | [licence](https://github.com/mono/mono/blob/main/LICENSE) |
| Mono I18N | `MMO/Plugins/I18N.dll, MMO/Plugins/I18N.West.dll` | MIT | Mono class libraries, MIT per the Mono licence | [licence](https://github.com/mono/mono/blob/main/LICENSE) |

## Notes for review

`SerializeRegistrySourceGenerator.dll` ships as a compiled library with no source
alongside it. It is built from LiteNetLibManager's own
[source generator project](https://github.com/insthync/LiteNetLibManager/tree/main/Scripts/SourceGenerators/SerializeRegistrySourceGenerator~),
which is MIT, and the same binary is published at the same path in that repository.
It is therefore covered by the bundled
[LiteNetLibManager licence](ThirdParty/LiteNetLibManager/LICENSE).

The native SQLite libraries under `MMO/Plugins` are public domain, but Asset Store
guideline 1.5.a restricts executables, so they are the most likely thing for a
reviewer to query.
