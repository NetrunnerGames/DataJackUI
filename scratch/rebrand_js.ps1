$file = "src/DataJackUIPlugin/public/datajackui.js"
$text = [System.IO.File]::ReadAllText($file)
$newText = $text.Replace("LuaTools", "DataJackUI").Replace("luatools", "datajackui")
[System.IO.File]::WriteAllText($file, $newText, [System.Text.Encoding]::UTF8)
Write-Host "Rebranded datajackui.js length: $($newText.Length) bytes" -ForegroundColor Green
