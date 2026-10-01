# 技术方案

## 架构

界面仍是 WPF。文案集中在 `UiText`：一份英文、一份中文。XAML 用 `{Binding [键], Source={x:Static UiText.Current}}` 绑定静态文字。视图模型里会随操作变化的句子在读取时调用 `UiText.Get` / `UiText.Format`。语言切换时 `UiText` 通知绑定，并让 `ObservableObject` 刷新已缓存的句子。

应用默认语言是中文。测试程序集用模块初始化把语言钉在英文，因此现有渲染测试继续检查原来的英文句子。

## 外观

`Themes/Studio.xaml` 提供页标题、说明、圆角主按钮、次按钮和胶囊页签。背景保持白色，避免渲染测试把浅灰背景当成墨迹。向导控件的行高、边距不改。

## 测试

- 英文键值与渲染测试依赖的句子一致。
- `UiTextTests` 检查中文说明里出现「麦克风」「空格」，并且项目状态句能格式化。
- 渲染测试仍跑 `AudioOptimizer.Tests.Ui`。
