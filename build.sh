#!/bin/bash

echo "update all submodules"
git submodule update --init
echo "create buildable monogame projects"
#cd Monogame/
mono Protobuild.exe
cd ../
echo "replace so files for android monogame"
cp -rf "MonoGame.Framework.Android.csproj" "MonoGame/MonoGame.Framework/MonoGame.Framework.Android.csproj"

echo "run ios project"
msbuild CocosSharp.iOS.sln /p:Platform=iPhone /p:Configuration=Release
echo "run android project"
msbuild CocosSharp.Android.sln /p:Configuration=Release
