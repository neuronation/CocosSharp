#!/bin/bash

MUSTBUILD="0"
while [ "$1" != "" ]; do
    case $1 in
        -build )        shift
        MUSTBUILD="1"
        ;;
    esac
    shift
done

git checkout master
git pull
echo "update all submodules"
git submodule update --init
echo "create buildable monogame projects"
cd Monogame/
mono Protobuild.exe
cd ../
echo "replace so files for android monogame"
cp -rf "MonoGame.Framework.Android.csproj" "MonoGame/MonoGame.Framework/MonoGame.Framework.Android.csproj"
cp -rf "Lidgren.Network.Android.csproj" "MonoGame/ThirdParty/Lidgren.Network/Lidgren.Network.Android.csproj"
mkdir -p "MonoGame/ThirdParty/Dependencies/openal-soft/libs/x86_64"
cp -rf "nn_patch/x86_64/libopenal32.so" "MonoGame/ThirdParty/Dependencies/openal-soft/libs/x86_64"
mkdir -p "MonoGame/ThirdParty/Dependencies/openal-soft/libs/arm64-v8a"
cp -rf "nn_patch/arm64-v8a/libopenal32.so" "MonoGame/ThirdParty/Dependencies/openal-soft/libs/arm64-v8a"

echo "get latest version of the exercise engine"
cd app_c_sharp_exercise_engine/
git checkout master
git pull
cd ../

if [ "$MUSTBUILD" -eq "1" ]; then
    echo "run PCL project"
    msbuild PCL/CocosSharp.PCL.sln /p:Configuration=Release
    echo "run ios project"
    msbuild CocosSharp.iOS.sln /p:Platform=iPhone /p:Configuration=Release
    echo "run android project"
    msbuild CocosSharp.Android.sln /p:Configuration=Release

    echo "push the new cocosengine"
    cd app_c_sharp_exercise_engine
    git add --all
    git commit -m "Cocosengine update"
    git push

    cd ../
    git add --all
    git commit -m "Cocosengine update"
    git push
fi
