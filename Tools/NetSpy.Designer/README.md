# NetSpy Designer

Program separat pentru a proiecta aspectul NetSpy: culori, fonturi, colțuri rotunjite (WinUI),
stilul taburilor, bara de titlu, densitatea arborelui. În stânga e o previzualizare vie a ferestrei NetSpy.

- Rulare: `dotnet run -c Release` (din acest folder).
- Designul se salvează automat în `%LocalAppData%\NetSpy\Designer\autosave.netspy-design.json`;
  „Salvează ca…” scrie un fișier `*.netspy-design.json`.
- Verificare fără interfață: `NetSpy.Designer.exe --snapshot poza.png [indexTemă] [Cheie=valoare ...]`.
- Aplicare în NetSpy: butonul „Exportă în NetSpy” (sau `--export design.json`) scrie tema în
  `NetSpy\NetSpy\Themes`; `--convert-icons` scrie iconițele arborelui în `NetSpy\NetSpy\Images\NetSpy`.

Iconițele din `Icons\` provin dintr-un proiect open-source sub licența MIT (autorii și textul licenței:
`Icons\LICENSE-MIT.txt`), la rândul lor din Visual Studio 2017 Image Library.
Sunt randate cu SharpVectors (BSD-3-Clause).
