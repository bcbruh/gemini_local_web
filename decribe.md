Mục tiêu của sản phẩm là tạo một local web app dùng Gemini Web làm “brain” cho một agent chạy trên máy người dùng. Người dùng chỉ cần tải một file .exe, mở lên, kết nối tài khoản Gemini, chọn một thư mục làm workspace và bắt đầu chat. Toàn bộ phần đọc file, sửa code, search project, chạy lệnh, xem diff, quản lý quyền và lưu lịch sử đều diễn ra local; Gemini Web chỉ nhận context cần thiết, suy luận và quyết định bước tiếp theo.

Triết lý chính của sản phẩm là:

Gemini Web = brain. Local runtime = body. Local web = interface. User = người kiểm soát quyền.

1. Mục tiêu trải nghiệm

Sản phẩm phải hướng tới gần như zero-config. Người dùng không cần cài Python, không cần terminal, không cần API key, không cần biết endpoint, model provider, cookie name hay bất kỳ chi tiết kỹ thuật nào của Gemini Web.

Flow lý tưởng là:

Download → mở .exe → Connect Gemini → Choose folder → Chat.

Khi .exe được mở, ứng dụng tự khởi động một backend local trên 127.0.0.1, chọn một port đang trống và tự mở trình duyệt mặc định vào giao diện local. Khi ứng dụng đóng, server local cũng dừng.

Lần đầu sử dụng, người dùng chỉ cần kết nối Gemini và chọn workspace. Những lần sau, ứng dụng tự cố gắng khôi phục session Gemini, workspace gần nhất và cuộc trò chuyện đang có. Nếu session hết hạn thì chỉ hiện nút Reconnect Gemini.

Không nên có một màn hình Settings lớn. Những gì có thể tự suy ra hoặc có default an toàn thì nên tự xử lý.



2. Thiết kế frontend

Frontend nên là một trang web local tối giản, tập trung vào một cửa sổ chat duy nhất.

Không cần hệ thống nhiều conversation, không cần sidebar phức tạp, không cần dashboard. Sản phẩm nên tạo cảm giác như đang làm việc với một agent gắn với workspace hiện tại hơn là một chatbot tổng quát.

Phần đầu trang chỉ cần hiển thị trạng thái Gemini và workspace hiện tại. Ví dụ:

Gemini: Connected

Workspace: D:\Projects\my-app

Người dùng có thể đổi workspace khi muốn.

Phần chính là chat giữa user và Gemini. Ngoài message thông thường, giao diện cần thể hiện các action quan trọng của agent theo cách ngắn gọn và dễ hiểu, ví dụ:

Reading src/auth.py

Searching for login_handler

Running tests

Preparing changes

Không nên đổ toàn bộ raw log vào chat. Chi tiết kỹ thuật có thể nằm trong phần mở rộng như Details.

Khi Gemini đề xuất sửa file, frontend hiển thị diff trực tiếp trong cuộc trò chuyện. Người dùng có thể xem thay đổi rồi chọn Apply hoặc Reject nếu policy hiện tại yêu cầu xác nhận.

Trạng thái đang xử lý cũng cần rõ ràng. Ví dụ Gemini đang chờ command hoàn tất, đang đọc file hoặc đang suy luận tiếp. Không nên để user tưởng app bị treo.

Giao diện phải responsive nhưng ưu tiên desktop vì use case chính là làm việc với code và filesystem.



3. Một cửa sổ lịch sử duy nhất

V1 chỉ giữ một session/conversation chính.

Khi ứng dụng mở lại, conversation đó được phục hồi. Người dùng tiếp tục làm việc trong cùng một luồng.

Có thể có một nút nhỏ như New session hoặc Clear conversation, nhưng không xây hệ thống quản lý nhiều thread.

History lưu local và nên chứa message của user, response của Gemini, tool action, tool result cần thiết, approval state và các thay đổi liên quan đến workspace.

Không lưu credential Gemini hoặc secret nhạy cảm vào history.

History cũng không nên giữ vô hạn nguyên văn mọi terminal output. Những output lớn cần được rút gọn hoặc lưu riêng để tránh conversation phình lên.



4. Kết nối Gemini Web

Gemini Web là model backend duy nhất của sản phẩm trong giai đoạn đầu.

Local app gửi context tới Gemini Web, nhận response, phân tích xem đó là câu trả lời cuối hay một yêu cầu sử dụng local tool. Nếu Gemini cần thêm thông tin, runtime thực hiện action rồi gửi observation trở lại Gemini.

Một nguyên tắc quan trọng là toàn bộ chi tiết authentication phải bị giấu khỏi user và khỏi các phần khác của hệ thống.

Người dùng chỉ nhìn thấy:

Connect Gemini

Connected

Reconnect

Disconnect

Không nên yêu cầu người dùng tự vào DevTools, tìm cookie rồi paste vào file cấu hình.

UX mục tiêu là khi bấm Connect Gemini, một browser hoặc profile dành riêng cho việc đăng nhập Gemini được mở. User đăng nhập Google như bình thường. Khi app xác nhận session hoạt động thì browser có thể đóng hoặc được giữ lại tùy implementation.

Nếu backend hiện tại vẫn cần lấy một số session credential từ browser session, việc đó phải được thực hiện nội bộ. Frontend không bao giờ được nhìn thấy chúng.



5. Tách Gemini khỏi agent core

Agent core không được phụ thuộc trực tiếp vào cách Gemini Web authentication hoặc request protocol đang hoạt động.

Đối với agent, Gemini chỉ nên là một brain có khả năng nhận context và trả response.

Điều này rất quan trọng vì Gemini Web không phải API ổn định được thiết kế cho use case này. Google có thể thay endpoint, request format, authentication requirement, anti-bot behavior hoặc cách conversation hoạt động.

Khi điều đó xảy ra, chỉ lớp kết nối Gemini cần thay đổi. File tools, agent loop, history, frontend, workspace và security không nên bị ảnh hưởng.

Đây là một trong những nguyên tắc kiến trúc quan trọng nhất của project.



6. Agent loop

Core feature của sản phẩm là vòng lặp agent.

Người dùng đưa ra một task, ví dụ:


kiểm tra vì sao login bị lỗi rồi sửa giúp tôi.



Local runtime gửi request cùng context ban đầu tới Gemini.

Gemini có thể yêu cầu search project.

Runtime thực hiện search rồi gửi kết quả lại.

Gemini có thể yêu cầu đọc một hoặc vài file.

Runtime đọc các file đó rồi gửi lại.

Gemini có thể yêu cầu chạy test.

Runtime kiểm tra policy, chạy test và trả output.

Gemini sau đó đề xuất patch.

Runtime kiểm tra patch, tạo diff và nếu cần thì xin approval.

Sau khi apply, Gemini có thể yêu cầu chạy test lần nữa.

Quá trình tiếp tục cho đến khi Gemini trả một final response.

Về trải nghiệm, user chỉ thấy agent đang làm việc với project giống các coding agent hiện đại, nhưng reasoning vẫn đến từ Gemini Web.



7. Tool set ban đầu

V1 không cần quá nhiều tool. Càng nhiều tool từ đầu càng làm agent khó kiểm soát và tăng attack surface.

Bộ khả năng ban đầu chỉ cần đủ để giải quyết phần lớn coding workflow: đọc file, xem nội dung thư mục, search file/source, tạo patch, chạy command phục vụ development và xem git diff.

Nên ưu tiên patch thay cho rewrite toàn bộ file. Patch dễ review hơn, giảm lượng dữ liệu gửi qua lại và giúp rollback dễ hơn.

Xóa file phải được xem là một action riêng có mức rủi ro cao hơn edit.

Các khả năng như browser automation, MCP, image/screenshot understanding, process management, git push, deployment hoặc remote machine nên để sau khi core coding agent đã ổn định.



8. Context management

Đây là một trong những phần ảnh hưởng lớn nhất tới chất lượng agent.

Gemini không được tự động nhận toàn bộ workspace. Local runtime phải chỉ gửi những gì cần thiết.

Khi user gửi một task, Gemini có thể nhận một context nhẹ như tên workspace, một project summary ngắn, vài file đang active hoặc một directory overview.

Sau đó Gemini dùng tools để tự khám phá project.

Ví dụ thay vì gửi 500 file lên ngay từ đầu, Gemini search một symbol, đọc ba file liên quan, chạy một test và từ đó quyết định cần đọc tiếp gì.

Cách này vừa tiết kiệm context vừa giảm lượng dữ liệu của user được gửi ra ngoài máy.

Agent cũng phải quản lý context dài hạn. Khi conversation dài, terminal logs cũ, tool results lặp lại hoặc nội dung file cũ cần được compact hoặc loại bỏ.

Một file đã thay đổi cũng không được tiếp tục dùng snapshot cũ trong context mà không kiểm tra version.



9. Workspace model

Mỗi session làm việc gắn với một workspace do user chọn.

Mặc định agent chỉ được phép thao tác bên trong workspace đó.

Nếu user chọn:

D:\Projects\App

thì runtime chỉ tự động cho phép đọc/search/edit bên trong vùng này.

Không được dựa vào string comparison đơn giản để kiểm tra path. Path phải được resolve thành canonical path trước khi kiểm tra nhằm tránh các trường hợp .., symbolic link hoặc junction khiến agent thoát khỏi workspace.

Agent cũng không nên tự động đọc các thư mục nhạy cảm như .git internals, credential stores hoặc secret file nếu không thực sự cần.

Các file như .env, key, certificate hoặc credential config cần có handling đặc biệt để tránh vô tình gửi secret lên Gemini.



10. Permission system

Sản phẩm nên có security mạnh nhưng UI permission phải đơn giản.

Một model hợp lý là ba chế độ:

Ask before changes

Auto-edit workspace

Read only

Ask before changes là default.

Trong chế độ này, các thao tác đọc/search thông thường có thể tự chạy. Những thay đổi file được chuẩn bị dưới dạng diff và chờ user approve.

Auto-edit workspace cho phép agent tự áp dụng những edit hợp lệ bên trong workspace nhưng các hành động rủi ro cao vẫn phải hỏi.

Read only chỉ cho Gemini phân tích project.

Ngoài setting user nhìn thấy, backend vẫn phải có policy cứng. Một lựa chọn “auto” không có nghĩa Gemini được quyền làm bất cứ thứ gì.



11. Command execution

Shell là một trong những phần nguy hiểm nhất của local agent.

Không bao giờ lấy một đoạn text Gemini tạo ra rồi gửi trực tiếp vào shell mà không qua policy layer.

Command cần được phân loại theo rủi ro.

Những lệnh phục vụ inspection hoặc test trong workspace có thể có policy nhẹ hơn.

Những hành động như xóa lượng lớn dữ liệu, cài package hệ thống, thay đổi network config, chạy script tải từ Internet, đẩy code lên remote hoặc thao tác ra ngoài workspace cần approval hoặc bị chặn.

Runtime phải có timeout cho command và giới hạn output. Một process không được phép treo agent vô hạn.

User phải thấy command quan trọng nào đang được thực hiện.



12. Secret handling

Local agent có khả năng đọc source code nên nguy cơ vô tình lấy secret là rất thật.

Các loại dữ liệu có khả năng là secret nên được phát hiện và xử lý cẩn thận, đặc biệt .env, private key, token, credential file và browser/session data.

Không nên tự động gửi toàn bộ nội dung của các file này tới Gemini.

Nếu một task thực sự cần thông tin trong đó, app có thể redact value nhạy cảm hoặc yêu cầu user xác nhận.

Logs cũng phải có secret redaction.

Một exception hoặc debug dump không được phép in credential Gemini.



13. Credential storage

Gemini session credential cần được lưu bằng cơ chế bảo vệ của hệ điều hành thay vì plaintext config.

Trên Windows nên tận dụng credential protection được gắn với Windows user hiện tại.

Credential chỉ được decrypt trong process khi cần.

Frontend không được có endpoint trả raw credential.

Nút Disconnect Gemini phải xóa credential/session tương ứng.

Nếu có thể chuyển sang giữ session hoàn toàn trong một dedicated browser profile mà không cần export raw cookie thì đây là hướng tốt hơn về lâu dài.



14. Local web security

localhost không đồng nghĩa với an toàn tuyệt đối.

Server chỉ được listen trên loopback interface như 127.0.0.1, không mở mặc định ra LAN bằng 0.0.0.0.

Mỗi lần app khởi động nên tạo một session ngẫu nhiên để browser được ứng dụng mở có quyền truy cập local API.

Backend phải kiểm tra origin cho những request nhạy cảm và không dùng CORS wildcard.

Các endpoint có khả năng đọc file, sửa file hoặc chạy command đặc biệt cần được bảo vệ.

Một website khác đang mở trong browser không được phép đơn giản gọi localhost và điều khiển agent.

Port ngẫu nhiên mỗi session cũng giúp tránh conflict và giảm việc một service cố định luôn mở.



15. Backend local

Backend là bộ điều phối toàn bộ ứng dụng.

Nó quản lý web session, agent state, Gemini connection, tool execution, permissions, workspace, context, history và error recovery.

Frontend chỉ là giao diện điều khiển backend.

Backend cũng chịu trách nhiệm stream trạng thái lên UI để user nhìn thấy Gemini đang làm gì mà không cần refresh trang.

Một task đang chạy phải có khả năng cancel.

Nếu browser bị đóng nhưng backend vẫn đang chạy, user mở lại local page vẫn nên thấy state hiện tại.



16. Error handling

Vì Gemini Web integration có thể thay đổi, error handling cần được thiết kế từ đầu.

Các lỗi nên được phân biệt rõ giữa session hết hạn, network failure, Gemini Web thay đổi response format, rate limit, tool failure, command timeout, invalid patch và workspace permission violation.

Không nên hiện raw stack trace cho user bình thường.

UX nên nói được những câu rõ ràng như:

Gemini session expired — reconnect required.

The command timed out.

Gemini requested access outside the workspace. The action was blocked.

The file changed since Gemini last read it. Re-reading before applying the patch.

Đồng thời developer mode có thể lưu diagnostic log đã được redact secret.



17. Handling file changes và race condition

Agent không nên assume file vẫn giống lúc Gemini đọc nó.

Nếu user tự sửa file trong editor giữa lúc Gemini đang làm việc, patch có thể không còn hợp lệ.

Trước khi apply patch cần kiểm tra file version/hash hiện tại.

Nếu file đã thay đổi, agent nên đọc lại file rồi yêu cầu Gemini tính lại patch thay vì ghi đè.

Điều này giúp tránh một trong những lỗi khó chịu nhất của coding agent: phá mất thay đổi người dùng vừa thực hiện.



18. Diff và rollback

Mỗi modification nên được mô tả dưới dạng diff rõ ràng.

User có thể xem file nào thay đổi và phần nào bị thêm/xóa.

Trước khi thực hiện một batch edit, runtime nên giữ snapshot đủ để rollback nếu apply thất bại.

Không cần xây version-control system riêng; chỉ cần đảm bảo một action có thể được hoàn tác an toàn khi chưa chuyển sang nhiều bước tiếp theo.

Nếu workspace đang dùng Git thì có thể tận dụng trạng thái Git để hiển thị thay đổi, nhưng agent không nên assume mọi folder đều là Git repository.



19. Gemini response protocol

Vì Gemini Web không nhất thiết cung cấp native tool calling theo cách mà local app cần, project nên có protocol riêng giữa Gemini và agent runtime.

Gemini được hướng dẫn rằng khi cần local action thì phải tạo một structured request thay vì mô tả bằng prose.

Runtime parse request đó, validate schema, kiểm tra permission rồi mới thực thi.

Nếu Gemini tạo output sai format thì runtime không được cố đoán một command nguy hiểm. Nó có thể yêu cầu Gemini sửa lại format.

Protocol phải version được để sau này thêm tool mà không phá session cũ.



20. Prompt robustness

System instruction gửi Gemini phải mô tả rõ quyền hạn và giới hạn của agent.

Gemini cần biết nó không trực tiếp truy cập máy, chỉ có thể yêu cầu local tools.

Nó cũng phải biết rằng tool result mới là source of truth.

Không nên cho Gemini tự tuyên bố “tôi đã sửa file” nếu chưa nhận confirmation từ tool runtime.

Prompt cần chống việc nội dung trong source file giả làm system instruction. Code, README, comment hoặc website content phải được coi là untrusted data chứ không phải instruction điều khiển agent.



21. Session recovery

Một task dài có thể bị gián đoạn vì browser đóng, machine sleep hoặc Gemini session timeout.

App nên lưu đủ state để ít nhất phục hồi chat và workspace.

Nếu một tool đang chạy tại thời điểm app crash, khi mở lại không được assume nó đã thành công.

Các action chưa có confirmation phải được coi là incomplete.

Gemini Web conversation và local history cũng có thể mất đồng bộ. Local runtime nên là source of truth về những tool action thực sự đã xảy ra trên máy.



22. Packaging

Sản phẩm cuối hướng tới một .exe Windows.

Executable chứa backend và frontend assets, tự start local server rồi mở system browser.

Không cần bundled Chromium chỉ để hiển thị UI, giúp ứng dụng nhẹ hơn.

Nếu flow login sau này cần dedicated browser automation thì browser component có thể được quản lý riêng, nhưng không nên biến toàn bộ application thành một browser bundle nếu không cần.

App không yêu cầu user mở command prompt.



23. Installation UX

Giai đoạn đầu có thể phát hành portable executable.

User tải xuống rồi chạy.

Sau này có thể có installer để tạo Start Menu shortcut, auto-update và Windows context menu.

Một feature rất hợp với sản phẩm về sau là:

Right click folder → Open with Gemini Local

Khi đó ứng dụng tự chọn folder đó thành workspace và mở chat ngay.

Nhưng đây không phải yêu cầu của MVP.



24. Update strategy

Vì Gemini Web có thể thay đổi bất kỳ lúc nào, khả năng update nhanh là rất quan trọng.

Phiên bản production nên có một cơ chế kiểm tra bản cập nhật mới, ít nhất là thông báo khi có release mới.

Nếu Gemini adapter hỏng do thay đổi phía Google, người dùng cần nhận được thông báo rõ rằng đây là compatibility issue thay vì tưởng project của họ bị lỗi.

Không tự động update binary một cách mù quáng nếu chưa có signature verification.



25. Privacy

App cần nói rõ với user rằng source code chỉ nằm local trừ phần context được gửi tới Gemini để xử lý.

Không nên gửi telemetry chứa code hoặc prompt mặc định.

Nếu có analytics thì phải giới hạn ở dữ liệu phi nội dung như app version hoặc loại error, và tốt nhất là opt-in.

Người dùng nên hiểu rõ file nào được gửi lên Gemini khi agent đọc chúng.

Về sau có thể cung cấp một activity log như:

Sent 3 file excerpts to Gemini

thay vì âm thầm gửi dữ liệu.



26. Performance

App phải mở nhanh.

Không index toàn bộ project ngay khi user chọn folder nếu chưa cần.

File search nên tận dụng các phương pháp nhanh và ignore các thư mục rõ ràng không cần thiết như dependency cache, build output hoặc .git.

Các file binary và file cực lớn không nên được gửi trực tiếp vào context.

Frontend không được freeze khi terminal command hoặc Gemini request đang chạy.

Streaming response từ Gemini nếu có thể nên được tận dụng để tạo cảm giác phản hồi nhanh.



27. Các bài học kỹ thuật cần chủ động phòng tránh

Có một số vấn đề nên được coi là requirement ngay từ đầu chứ không chờ xảy ra mới vá.

Gemini session có thể hết hạn bất ngờ, nên reconnect phải là first-class flow.

Web backend của Gemini có thể đổi, nên adapter phải cô lập.

Terminal output có thể cực lớn, nên phải truncate/stream.

Context có thể tăng vô hạn, nên phải compact.

Một file có thể thay đổi giữa lúc read và write, nên cần version checking.

Localhost API có thể bị website khác tấn công, nên cần session/origin protection.

Cookie hoặc secret có thể vô tình lọt vào log, nên logging phải redact từ đầu.

Gemini có thể tạo malformed tool call, nên parser phải fail closed thay vì đoán.

Gemini có thể bị prompt injection từ codebase, nên tool policy không bao giờ được giao hoàn toàn cho model.



28. MVP

MVP đầu tiên chỉ cần chứng minh được end-to-end loop.

User mở app, kết nối Gemini, chọn folder và hỏi một câu về một file.

Gemini yêu cầu đọc file.

Runtime đọc file trong workspace và trả nội dung lại.

Gemini trả lời user.

Khi vòng này ổn định thì thêm search.

Sau đó thêm patch.

Sau đó thêm diff + approval.

Sau đó thêm chạy test/command.

Sau đó mới thêm Git awareness.

Frontend đẹp, installer, tray icon hoặc advanced tools đều đứng sau những milestone này.



29. V1 hoàn chỉnh

V1 có thể được coi là đủ dùng khi người dùng có thể mở một project và yêu cầu agent thực hiện những task coding thông thường như hiểu code, tìm bug, sửa một số file, chạy test và giải thích kết quả.

V1 cần có workspace sandbox, diff approval, session restore, Gemini reconnect, local history, command timeout và secret protection.

Không cần đạt parity với các coding agent lớn ngay lập tức.

Điều quan trọng là vòng agent đáng tin cậy và UX đơn giản.



30. Những thứ chưa nên làm

Để tránh project bị phình quá sớm, giai đoạn đầu không nên tập trung vào multi-agent, nhiều model provider, cloud account, cloud history, multi-user, plugin marketplace, deployment automation, browser computer-use toàn diện, remote agents hoặc nhiều conversation.

Mỗi feature đó có thể được thêm sau khi nền móng agent đã chắc.

Sản phẩm phải giữ được sự đơn giản ban đầu.



31. Hướng phát triển sau V1

Khi coding workflow đã ổn định, có thể thêm khả năng hiểu screenshot, browser tool, MCP, richer Git workflow, project memory, project-level instructions và integration với editor.

Có thể thêm mode để agent tự chạy lâu hơn với ít confirmation hơn, nhưng vẫn phải giữ policy giới hạn workspace và dangerous actions.

Một extension/editor integration về sau chỉ nên là một frontend khác kết nối tới cùng local agent runtime, không tạo một core khác.



32. Tiêu chuẩn thành công

Sản phẩm đạt đúng mục tiêu khi một người không hiểu API vẫn có thể tải file, mở lên và bắt đầu sử dụng trong vài phút.

Họ không cần biết Gemini authentication hoạt động ra sao.

Họ không cần biết local server đang chạy ở port nào.

Họ không cần quản lý config.

Họ chỉ cần biết ba thứ:

Gemini đã kết nối chưa?

Tôi đang cho agent làm việc trong folder nào?

Agent đang định thay đổi điều gì?

Phần còn lại phải được ứng dụng tự xử lý.



Tóm lại, sản phẩm nên hướng tới một trải nghiệm rất đơn giản ở bề mặt nhưng có một runtime local khá nghiêm ngặt bên dưới:

một executable → một local web → một Gemini session → một workspace → một conversation → một agent có khả năng đọc, sửa và chạy project trong phạm vi được kiểm soát.

Mọi quyết định thiết kế nên phục vụ hai mục tiêu song song: giảm tối đa số thứ user phải cấu hình và không đánh đổi ranh giới bảo mật chỉ để UX trông đơn giản hơn.
