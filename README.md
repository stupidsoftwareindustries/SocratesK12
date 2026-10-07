# INSTALLATION and FIRST USE GUIDE

1. Download the .exe from the releases tab or click [here](https://github.com/stupidsoftwareindustries/SocratesK12/releases/latest/download/SocratesK12.exe)
2. The .exe should be run as administrator and should automatically run as administrator. However, if it does not: right click the file, and click on "Run as Administrator" in the options list.
3. Click the underlined text that says "More info".
4. Click the now available "Run anyway" button at the bottom right.

# BUILD IT YOURSELF (ignore if you do not understand)

1. Download all the files in the repository except for the README.md file and put it in a folder
2. Open that folder in CMD and run:
   'dotnet publish -c Release'
3. Head go through \bin\Release\net8.0-windows\win-x64 and then find the folder named "publish" and open it
4. There is your standalone .exe!
