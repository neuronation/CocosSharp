#!/bin/bash

echo "update all submodules"
git submodule update --init
echo "create buildable monogame projects"
cd Monogame/
mono Protobuild.exe
cd ../
echo "replace so files for android monogame"
cp -rf "MonoGame.Framework.Android.csproj" "MonoGame/MonoGame.Framework/MonoGame.Framework.Android.csproj"

echo "get latest version of the exercise engine"
cd app_c_sharp_exercise_engine/
git pull
cd ../

echo "run ios project"
msbuild CocosSharp.iOS.sln /p:Platform=iPhone /p:Configuration=Release
echo "run android project"
msbuild CocosSharp.Android.sln /p:Configuration=Release

echo "push the new cocosengine"
cd app_c_sharp_exercise_engine
git add --all
git commit -m "Cocosengine update"
git push
