# Copilot Instructions

## Project Guidelines
- The user wants all UI text kept in English.
- UI text should remain in English; header notifications should appear in the MainLayout header as plain colored text without boxed alert styling, and tab headers should remain always visible/sticky while only the panel content scrolls.
- The top tab/menu header must never scroll with its content; keep the tab strip in a separate fixed container above a separately scrollable content area.
- For this project’s Clicker UI, option groups should be collapsible by default to save space.
- For this repo's Clicker UI, the interaction-region preview should show the cropped region screenshot, and protected zones should be created from two real clicks inside the marked window with immediate overlay feedback.

## Functional Reliability
- When a functional regression is reported, prioritize immediate rollback or fix and validate the multimodal Clicker flow reliability end-to-end before concluding.

## Build and Maintenance
- When asked to fix warnings in this repo, iterate with rebuilds until the warning count is zero and verify with a full rebuild.
- For llama.cpp backend detection in this repo, prefer checking the installed runtime-specific DLLs in the llama.cpp directory directly (for example cublas64_13.dll, cublasLt64_13.dll, cudart64_13.dll) to infer CUDA backend/version.
- For this repo's llama.cpp updater, note that CUDA runtime update packages do not contain llama-server.exe and must be handled without expecting an executable/binary folder.