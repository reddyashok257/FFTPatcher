"C:\Program Files (x86)\Microsoft Visual Studio 12.0\Common7\IDE\devenv.com" FFTPatcher.sln /rebuild "Release|x86"
rmdir /s /q staging
mkdir staging
xcopy /s /Y bin\x86\Release staging
xcopy /s /Y FFTacText\bin\x86\Release\*.* staging
xcopy /s /Y FFTorgASM\bin\x86\Release\*.* staging
xcopy /s /Y ShishiSpriteEditor\bin\x86\Release\*.* staging
xcopy /s /Y MassHexASM\bin\x86\Release\*.* staging
del staging\*.pdb
del staging\*.vshost.exe
del staging\*.vshost.exe.config
del staging\*.manifest
