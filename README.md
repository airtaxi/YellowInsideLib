# YellowInsideLib

[![NuGet](https://img.shields.io/nuget/v/YellowInsideLib.svg)](https://www.nuget.org/packages/YellowInsideLib/)

메신저 채팅창에 사이드카 형태로 기능을 확장할 수 있는 .NET 라이브러리입니다.현재는 디시콘(Dccon) 버튼 부착 및 이미지 전송 기능을 제공하며, 향후 다양한 기능이 추가될 수 있습니다.

## 요구 사항

- .NET 10 (Windows)

## 지원 클라이언트

실행 중인 프로세스로 클라이언트 종류를 자동 판별합니다.

- MFC 기반 클라이언트 (`KakaoTalk.exe`)
- Qt 기반 클라이언트 (`KakaoTalkUI.exe`): Windows UI Automation으로 입력 툴바의 버튼 위치를 찾아 디시콘 버튼을 배치합니다.
  - Qt 채팅창은 `WM_DROPFILES`를 처리하지 않으므로, 이미지는 탐색기에서 파일을 끌어다 놓을 때와 같은 OLE 드래그앤드롭으로 전송합니다. 여러 장을 한 번에 보낼 수 있습니다.
  - 전송하는 동안 마우스 커서가 잠깐 채팅창 안으로 이동했다가 원래 위치로 돌아옵니다. 이때 마우스를 움직이면 전송이 취소되고, `SendMethod.Auto`에서는 클립보드 방식으로 다시 시도합니다.
  - 메신저 설정에서 파일 전송 전 확인을 켜 두었다면 확인 창이 표시되며, 전송은 사용자가 직접 확정해야 합니다.

## 사용법

`SessionManager`는 싱글톤으로 제공되며, 채팅창 감지 및 세션 생성/제거를 자동으로 관리합니다. 사용자는 이벤트를 구독하고 서비스를 시작하기만 하면 됩니다.

```csharp
using YellowInsideLib;

// 인스턴스 획득
var manager = SessionManager.Instance;

// 이벤트 등록 (DcconButtonClicked 외의 이벤트는 선택 사항입니다)
manager.Log += message => Console.WriteLine(message);
manager.SessionCreated += session => Console.WriteLine($"세션 생성: {session}");
manager.SessionRemoved += session => Console.WriteLine($"세션 제거: {session}");
manager.DcconButtonClicked += session =>
{
    Console.WriteLine($"디시콘 버튼 클릭: {session}");
    // 디시콘 이미지 전송
    _ = manager.SendDcconAsync(session.ChatHwnd, @"C:\path\to\dccon.png");
};

// 서비스 시작 (버튼 아이콘 경로는 선택 사항)
manager.Start(buttonIconPath: @"C:\path\to\icon.png");

// 현재 세션 목록 조회
foreach (var session in manager.GetSessions())
    Console.WriteLine(session);

// 특정 채팅창에 수동으로 버튼 부착
manager.TryAttach(chatWindowHandle);

// 서비스 종료
manager.Stop();
```

## 라이선스

이 프로젝트는 [MIT 라이선스](LICENSE.txt)로 배포됩니다.

## 면책 조항 (Disclaimer)

본 라이브러리는 (주)카카오와 어떠한 제휴나 연관 관계도 없습니다. 본 라이브러리는 메신저의 내부 코드를 수정하거나 역공학하지 않으며, Windows API를 통한 UI 후킹만을 사용하여 동작합니다. 이는 일반적인 Windows 자동화 기법의 범위 내에 해당합니다.

본 라이브러리의 사용이 메신저 서비스 이용약관에 부합하는지 여부는 사용자가 직접 확인해야 하며, 비공식 자동화 도구의 사용은 서비스 약관 위반으로 간주되어 계정 제재 등의 조치를 받을 수 있습니다. 본 라이브러리의 사용으로 발생하는 모든 결과에 대한 책임은 전적으로 사용자에게 있습니다.

## 감사의 글 (Acknowledgement)

이 프로젝트는 [GitHub Copilot](https://github.com/features/copilot)의 도움으로 작성되었습니다.

## 작성자

**이호원** ([@airtaxi](https://github.com/airtaxi))
