# OctoConverter 배포 구성요소 및 고지

OctoConverter 자체 소스는 동봉된 `LICENSE`의 MIT 라이선스를 따릅니다.
외부 구성요소는 각각의 원본 라이선스를 따르며, 자체 소스의 MIT 라이선스로 재라이선스하지 않습니다.

## 설치 패키지에 포함되는 구성요소

이 self-contained Windows x64 배포에는 Microsoft.NETCore.App 및
Microsoft.WindowsDesktop.App **10.0.11**이 포함됩니다. 별도의 NuGet 라이브러리는 참조하지 않습니다.
각 패키지의 nuspec은 라이선스를 MIT로 표시하며 아래 원본을 변경 없이 동봉합니다.

| 구성요소 | 설치 폴더 내 원문 |
|---|---|
| Microsoft.NETCore.App.Runtime.win-x64 10.0.11 | `Licenses/dotnet-10.0.11/Microsoft.NETCore.App-LICENSE.TXT`, `Microsoft.NETCore.App-THIRD-PARTY-NOTICES.TXT` |
| Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.11 | `Licenses/dotnet-10.0.11/Microsoft.WindowsDesktop.App-LICENSE` |
| WindowsDesktop에 포함된 WPF | `Licenses/dotnet-10.0.11/wpf-LICENSE.TXT`, `wpf-THIRD-PARTY-NOTICES.TXT` |
| WindowsDesktop에 포함된 Windows Forms | `Licenses/dotnet-10.0.11/winforms-LICENSE.TXT`, `winforms-THIRD-PARTY-NOTICES.TXT` |

Microsoft 런타임에는 원본 THIRD-PARTY-NOTICES에 나열된 외부 코드의 고지가 함께 적용됩니다.
원본 고지에 명시된 개별 저작권과 이용 조건을 그대로 유지합니다.

원본 출처:

- [NETCore 공식 NuGet 패키지](https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.11)의 `LICENSE.TXT` 및 `THIRD-PARTY-NOTICES.TXT`.
- [WindowsDesktop 공식 NuGet 패키지](https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11)의 `LICENSE`.
- 두 패키지 nuspec에 기록된 소스: [dotnet/dotnet e2f47b0110ed922f21a1522da67279133ce28f32](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32).
- 위 커밋의 [`src/wpf`](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32/src/wpf) 및 [`src/winforms`](https://github.com/dotnet/dotnet/tree/e2f47b0110ed922f21a1522da67279133ce28f32/src/winforms)에서 원본 LICENSE.TXT / THIRD-PARTY-NOTICES.TXT를 가져왔습니다.

빌드는 실제 포함되는 런타임 버전과 이 고지의 버전이 다른 경우 중단합니다.
런타임을 갱신할 때 해당 버전의 원본 라이선스 및 고지도 함께 갱신해야 합니다.

## 설치 패키지에 포함하지 않는 외부 도구

- **FFmpeg/ffprobe**: 설치 패키지에 포함하지 않습니다. 사용자 요청 시 Gyan 또는
  BtbN의 full/GPL 빌드를 별도로 내려받아 독립 실행 파일로 실행합니다.
  해당 빌드의 라이선스 및 버전별 소스 정보는 [Gyan FFmpeg builds](https://www.gyan.dev/ffmpeg/builds/)와
  [BtbN FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds)를 참고하세요.
  사용자가 직접 지정한 별도 FFmpeg 빌드에도 그 빌드의 라이선스가 적용됩니다.
- **Microsoft Office / LibreOffice**: 문서 변환 시 사용자 PC에 이미 설치된 도구를 사용합니다.
  이 설치 패키지에 포함하지 않습니다.
- **LibVLC**: 이 프로그램에서 사용하거나 동봉하지 않습니다.

코드 서명은 OctoConverter 앱 EXE, 설치 EXE 및 제거 EXE에만 적용합니다.
Microsoft 런타임이나 FFmpeg 등 외부 구성요소를 OctoBrain 저작물로 재서명하지 않습니다.
