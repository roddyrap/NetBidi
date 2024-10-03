# NetiBidi - An implementation of the Unicode Bi-Directional algorithm for the .net runtime

![NetBidi Icon](banner.svg)

> [!IMPORTANT]
> This project is not affiliated with Unicode, Microsoft, or the .net runtime in any way.

## Description

This project is a fully managed implementation of the [Unicode Bidirectional Algorithm](https://unicode.org/reports/tr9/).
The project is coded entirely in C# (With unicode data being generated directly from their website using Mono & T4 templating language).

The project passes all of the tests [Unicode provides for the bidirecational argument](https://www.unicode.org/reports/tr41/tr41-34.html#Tests9), but it is not guarateed to be 100% compliant.
There are some documented edge-cases that are not implemented.

For the vast majority of use cases, especially when no explicit directional formatting characters are used, it should work, but please do not use it in sensitive places.

There is a problem with some of the Unicode algorithm rules, as they require the linesizes and soft-wrapping. The relevant rules were not implemented.

## Use instructions

I am not an active C# developer, and am not very familiar with the C# ecosystem. I know nuget is a thing, and I hope to put the relevant DLLs in there in the near future.

### CLI User

The NetBidiApp project provides a CLI utility with basic text-conversion features.

### C# Developer

The NetBidi DLL includes relevant functions for converting strings.

## Build instructions

The proect was programmed on a Linux PC, and should not require any modifications to compile using .net core on different machines.
The project doesn't use any platform-dependent code, and the only build-system is MSBuild, so it should work for Windows as well.

> [!WARNING]  
> Building & Editing the project yourself requires a lot of memory. It's also worth noting that trying to open the generated Unicode data files might cause your program/os to crash, because C# intellisense isn't memory efficient. The 24GB of RAM on my computer are not enough.
