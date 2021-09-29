﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Reactive.Bindings;
using Reactive.Bindings.Extensions;
using RucheHome.AviUtl.ExEdit;
using RucheHome.Text;
using RucheHome.Util;
using RucheHome.Util.Extensions.String;
using RucheHome.Voiceroid;
using VoiceroidUtil.Extensions;
using VoiceroidUtil.Services;
using VoiceroidUtil.TRToys;
using VoiceroidUtil.View;
using static RucheHome.Util.ArgumentValidater;

namespace VoiceroidUtil.ViewModel
{
    /// <summary>
    /// VOICEROIDの選択とその操作を提供する ViewModel クラス。
    /// </summary>
    public class VoiceroidViewModel : ViewModelBase
    {
        /// <summary>
        /// コンストラクタ。
        /// </summary>
        /// <param name="processes">VOICEROIDプロセスコレクション。</param>
        /// <param name="canUseConfig">各設定値を利用可能な状態であるか否か。</param>
        /// <param name="talkTextReplaceConfig">トークテキスト置換設定値。</param>
        /// <param name="exoConfig">AviUtl拡張編集ファイル用設定値。</param>
        /// <param name="appConfig">アプリ設定値。</param>
        /// <param name="uiConfig">UI設定値。</param>
        /// <param name="lastStatus">直近のアプリ状態値の設定先。</param>
        /// <param name="canModifyNotifier">
        /// 設定変更可能な状態であるか否かの設定先。
        /// </param>
        /// <param name="windowActivateService">ウィンドウアクティブ化サービス。</param>
        /// <param name="voiceroidActionService">
        /// VOICEROIDプロセスアクションサービス。
        /// </param>
        /// <param name="aviUtlFileDropService">
        /// AviUtl拡張編集ファイルドロップサービス。
        /// </param>
        public VoiceroidViewModel(
            IReadOnlyCollection<IProcess> processes,
            IReadOnlyReactiveProperty<bool> canUseConfig,
            IReadOnlyReactiveProperty<TalkTextReplaceConfig> talkTextReplaceConfig,
            IReadOnlyReactiveProperty<ExoConfig> exoConfig,
            IReadOnlyReactiveProperty<AppConfig> appConfig,
            IReadOnlyReactiveProperty<UIConfig> uiConfig,
            IReactiveProperty<IAppStatus> lastStatus,
            IReactiveProperty<bool> canModifyNotifier,
            IWindowActivateService windowActivateService,
            IVoiceroidActionService voiceroidActionService,
            IAviUtlFileDropService aviUtlFileDropService)
        {
            ValidateArgumentNull(processes, nameof(processes));
            ValidateArgumentNull(canUseConfig, nameof(canUseConfig));
            ValidateArgumentNull(talkTextReplaceConfig, nameof(talkTextReplaceConfig));
            ValidateArgumentNull(exoConfig, nameof(exoConfig));
            ValidateArgumentNull(appConfig, nameof(appConfig));
            ValidateArgumentNull(uiConfig, nameof(uiConfig));
            ValidateArgumentNull(lastStatus, nameof(lastStatus));
            ValidateArgumentNull(canModifyNotifier, nameof(canModifyNotifier));
            ValidateArgumentNull(windowActivateService, nameof(windowActivateService));
            ValidateArgumentNull(voiceroidActionService, nameof(voiceroidActionService));
            ValidateArgumentNull(aviUtlFileDropService, nameof(aviUtlFileDropService));

            this.LastStatus = lastStatus;
            this.WindowActivateService = windowActivateService;
            this.VoiceroidActionService = voiceroidActionService;

            this.IsTextClearing =
                this.MakeInnerPropertyOf(appConfig, c => c.IsTextClearing);
            this.VoiceroidExecutablePathes =
                this.MakeInnerPropertyOf(
                    uiConfig,
                    c => c.VoiceroidExecutablePathes,
                    notifyOnSameValue: true);

            // 表示状態のVOICEROIDプロセスコレクション
            this.VisibleProcesses =
                appConfig
                    .ObserveInnerProperty(c => c.VoiceroidVisibilities)
                    .Select(vv => vv.SelectVisibleOf(processes))
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);

            // VOICEROID選択コマンド実行可能状態
            // 表示状態のVOICEROIDプロセスが2つ以上あれば選択可能
            this.IsSelectVoiceroidCommandExecutable =
                this.VisibleProcesses
                    .Select(vp => vp.Count >= 2)
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);

            // VOICEROID選択コマンドのチップテキスト
            this.SelectVoiceroidCommandTip =
                this.VisibleProcesses
                    .Select(_ => this.MakeSelectVoiceroidCommandTip())
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);

            // 最適表示列数
            // 6プロセス単位で列数を増やす
            this.VisibleProcessesColumnCount =
                this.VisibleProcesses
                    .Select(vp => Math.Min(Math.Max(1, (vp.Count + 5) / 6), 3))
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);

            // 選択中VOICEROIDプロセス
            this.SelectedProcess =
                new ReactiveProperty<IProcess>(this.VisibleProcesses.Value.First())
                    .AddTo(this.CompositeDisposable);

            // UI設定周りのセットアップ
            this.SetupUIConfig(uiConfig, processes);

            // 選択プロセス状態
            this.IsProcessStartup =
                this
                    .ObserveSelectedProcessProperty(p => p.IsStartup)
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);
            this.IsProcessRunning =
                this
                    .ObserveSelectedProcessProperty(p => p.IsRunning)
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);
            this.IsProcessPlaying =
                this
                    .ObserveSelectedProcessProperty(p => p.IsPlaying)
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);
            this.IsProcessExecutable =
                Observable
                    .CombineLatest(
                        this.SelectedProcess,
                        uiConfig.ObserveInnerProperty(c => c.VoiceroidExecutablePathes),
                        (p, pathes) =>
                        {
                            var path = (p == null) ? null : pathes[p.Id]?.Path;
                            return (!string.IsNullOrEmpty(path) && File.Exists(path));
                        })
                    .ToReadOnlyReactiveProperty()
                    .AddTo(this.CompositeDisposable);
            var processSaving =
                this
                    .ObserveSelectedProcessProperty(p => p.IsSaving)
                    .DistinctUntilChanged();
            var processDialogShowing =
                this
                    .ObserveSelectedProcessProperty(p => p.IsDialogShowing)
                    .DistinctUntilChanged();

            // トークテキスト
            this.TalkText =
                new ReactiveProperty<string>(@"").AddTo(this.CompositeDisposable);
            this.TalkTextLengthLimit =
                new ReactiveProperty<int>(TextComponent.TextLengthLimit)
                    .AddTo(this.CompositeDisposable);
            this.IsTalkTextTabAccepted =
                this.MakeInnerPropertyOf(appConfig, c => c.IsTabAccepted);

            // TRT's拡張
            // プレビュー用の設定値の取得
            this.PreviewStyle =
                this.MakeInnerPropertyOf(appConfig, c => c.PreviewStyle);

            // TextWrapValueの設定。テキストボックスを折り返すかどうか
            this.IsTextSplitting =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.IsTextSplitting);
            this.IsTextWrapping =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.IsTextWrapping);
            this.TextWrapValue =
                this.IsTextSplitting.CombineLatest(
                    this.IsTextWrapping,
                    (s, w) => (s && w ? TextWrapping.Wrap : TextWrapping.NoWrap))
                .ToReadOnlyReactiveProperty();

            // PreviewTextMarginの設定
            this.PreviewLeftMargin =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.PreviewLeftMargin);
            this.PreviewRightMargin =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.PreviewRightMargin);
            this.PreviewText =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.Text);
            this.PreviewHorizontal =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewText, t => t.Horizontal);
            this.PreviewTextMargin =
                this.PreviewLeftMargin.CombineLatest(
                    this.PreviewRightMargin, this.PreviewHorizontal,
                    (l, r, h) => GetPreviewTextMargin(l, r, h))
                .ToReadOnlyReactiveProperty();

            // プレビューの前後ボタンの表示or非表示用
            this.IsMultiScenePreview =
                new ReactiveProperty<bool>(false).AddTo(this.CompositeDisposable);
            this.IsFirstScene =
                new ReactiveProperty<bool>(true).AddTo(this.CompositeDisposable);
            this.IsLastScene =
                new ReactiveProperty<bool>(false).AddTo(this.CompositeDisposable);

            // テキストボックスのキャレット位置を取得・設定する
            this.TalkTextSelectionStart =
                new ReactiveProperty<int>(0).AddTo(this.CompositeDisposable);
            
            // 値保持用の変数
            this.AfterInsertSelectionStart = -1;
            this.PrePreviewScene = "";
            this.PreTalkText = "";
            this.PreTalkTextLength = 0;
            
            // 一行ごとのプレビューテキストのストアのリスト
            this.PreviewTextList =
                new ObservableCollection<PreviewTextStore>();

            // UpdatePreviewが呼び出されるタイミング
            this.TalkText.Subscribe(v => TalkTextHandle()).AddTo(this.CompositeDisposable);
            this.TalkTextSelectionStart.Subscribe(v => SelectionStartHandle()).AddTo(this.CompositeDisposable);

            // UpdatePreviewの処理結果はそのままに直接PreviewTextListを更新
            this.PreviewFontSize =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewStyle, s => s.PreviewFontSize);
            this.PreviewFontSize
                .Subscribe(v => ReflectSettingToPreview())
                .AddTo(this.CompositeDisposable);

            this.PreviewLetterSpace =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewText, t => t.LetterSpace);
            this.PreviewLetterSpace
                .Subscribe(v => ReflectSettingToPreview())
                .AddTo(this.CompositeDisposable);

            this.PreviewFontUri =
                this.MakeInnerReadOnlyPropertyOf(this.PreviewText, t => t.PreviewFontUri);
            this.PreviewFontUri
                .Subscribe(v => ReflectSettingToPreview())
                .AddTo(this.CompositeDisposable);

            // 一つ前のプレビューシーンに戻るコマンド
            this.BackSceneCommand =
                this.MakeCommand(this.ExecuteBackSceneCommand);

            // 一つ後のプレビューシーンに進むコマンド
            this.NextSceneCommand =
                this.MakeCommand(this.ExecuteNextSceneCommand);

            // キャレット位置に改行・分割文字列を挿入するコマンド
            this.InsertSplitCommand =
                this.MakeCommand(this.ExecuteInsertSplitCommand);
            
            // アイドル状態設定先
            var idle = new ReactiveProperty<bool>(true).AddTo(this.CompositeDisposable);
            this.IsIdle = idle;

            // 音声保存コマンド処理中フラグ設定先
            var saveCommandBusy =
                new ReactiveProperty<bool>(false).AddTo(this.CompositeDisposable);

            // トークテキストファイルドロップコマンド処理中フラグ設定先
            var dropTalkTextFileCommandBusy =
                new ReactiveProperty<bool>(false).AddTo(this.CompositeDisposable);

            // 本体側のテキストを使う設定
            this.UseTargetText =
                this.MakeInnerPropertyOf(
                    appConfig,
                    c => c.UseTargetText,
                    new[] { canUseConfig, this.IsIdle }
                        .CombineLatestValuesAreAllTrue()
                        .ToReadOnlyReactiveProperty()
                        .AddTo(this.CompositeDisposable));

            // - 本体側のテキストを使う設定が有効
            // または
            // - 音声保存処理中かつ保存成功時クリア設定が有効
            // ならばトークテキスト編集不可
            this.IsTalkTextEditable =
                new[]
                {
                    this.UseTargetText,
                    new[] { saveCommandBusy, this.IsTextClearing }
                        .CombineLatestValuesAreAllTrue()
                        .DistinctUntilChanged(),
                }
                .CombineLatest(flags => flags.Any(f => f))
                .Inverse()
                .ToReadOnlyReactiveProperty()
                .AddTo(this.CompositeDisposable);

            // VOICEROID選択コマンドコレクション(要素数 10 固定)
            this.SelectVoiceroidCommands =
                new ReadOnlyCollection<ICommand>(
                    Enumerable.Range(0, 10)
                        .Select(
                            index =>
                                this.MakeCommand(
                                    () => this.ExecuteSelectVoiceroidCommand(index),
                                    this.IsSelectVoiceroidCommandExecutable,
                                    this.VisibleProcesses
                                        .Select(vp => index < vp.Count)
                                        .DistinctUntilChanged()))
                        .ToArray());

            // 前方/後方VOICEROID選択コマンド
            this.SelectPreviousVoiceroidCommand =
                this.MakeCommand(
                    this.ExecuteSelectPreviousVoiceroidCommand,
                    this.IsSelectVoiceroidCommandExecutable);
            this.SelectNextVoiceroidCommand =
                this.MakeCommand(
                    this.ExecuteSelectNextVoiceroidCommand,
                    this.IsSelectVoiceroidCommandExecutable);

            // 起動/終了コマンド
            this.RunExitCommand =
                this.MakeAsyncCommand(
                    this.ExecuteRunExitCommand,
                    canUseConfig,
                    this.IsIdle,
                    this.IsProcessStartup.Inverse(),
                    processSaving.Inverse(),
                    processDialogShowing.Inverse());

            // 再生/停止コマンド
            var playStopCommandHolder =
                new AsyncPlayStopCommandHolder(
                    new[]
                    {
                        canUseConfig,
                        this.IsIdle,
                        this.IsProcessRunning,
                        processSaving.Inverse(),
                        processDialogShowing.Inverse(),
                        dropTalkTextFileCommandBusy.Inverse(),
                    }
                    .CombineLatestValuesAreAllTrue()
                    .DistinctUntilChanged()
                    .ObserveOnUIDispatcher(),
                    () => this.SelectedProcess.Value,
                    () => talkTextReplaceConfig.Value.VoiceReplaceItems,
                    () => appConfig.Value.PreviewStyle.IsTextSplitting 
                        ? appConfig.Value.PreviewStyle.RemovePreviewStrings(this.TalkText.Value) 
                        : this.TalkText.Value,
                    () => appConfig.Value.UseTargetText);
            this.PlayStopCommand = playStopCommandHolder.Command;
            playStopCommandHolder.Result
                .Where(r => r != null)
                .Subscribe(async r => await this.OnPlayStopCommandExecuted(r))
                .AddTo(this.CompositeDisposable);

            // 保存コマンド
            var saveCommandHolder =
                new AsyncSaveCommandHolder(
                    new[]
                    {
                        canUseConfig,
                        this.IsIdle,
                        this.IsProcessRunning,
                        processSaving.Inverse(),
                        processDialogShowing.Inverse(),
                        new[]
                        {
                            this.TalkText
                                .Select(t => !string.IsNullOrWhiteSpace(t))
                                .DistinctUntilChanged(),
                            this.UseTargetText,

                            // 敢えて空白文を保存したいことはまず無いと思われるので、
                            // 誤クリック抑止の意味も込めて空白文は送信不可とする。
                            // ただし本体側の文章を使う場合は空白文でも保存可能とする。
                            // ↓のコメントを外すと(可能ならば)空白文送信保存可能になる。
                            //this
                            //    .ObserveSelectedProcessProperty(p => p.CanSaveBlankText)
                            //    .DistinctUntilChanged(),
                        }
                        .CombineLatest(flags => flags.Any(f => f))
                        .DistinctUntilChanged(),
                        dropTalkTextFileCommandBusy.Inverse(),
                    }
                    .CombineLatestValuesAreAllTrue()
                    .DistinctUntilChanged()
                    .ObserveOnUIDispatcher(),
                    () => this.SelectedProcess.Value,
                    () => talkTextReplaceConfig.Value,
                    () => exoConfig.Value,
                    () => appConfig.Value,
                    () => this.TalkText.Value,
                    aviUtlFileDropService);
            this.SaveCommand = saveCommandHolder.Command;
            saveCommandHolder.IsBusy
                .Subscribe(f => saveCommandBusy.Value = f)
                .AddTo(this.CompositeDisposable);
            saveCommandHolder.Result
                .Where(r => r != null)
                .Subscribe(async r => await this.OnSaveCommandExecuted(r))
                .AddTo(this.CompositeDisposable);

            // 再生も音声保存もしていない時をアイドル状態とみなす
            new[]
            {
                playStopCommandHolder.IsBusy,
                saveCommandHolder.IsBusy,
            }
            .CombineLatestValuesAreAllFalse()
            .DistinctUntilChanged()
            .Subscribe(
                f =>
                {
                    idle.Value = f;

                    // アイドル状態なら設定変更可能とする
                    canModifyNotifier.Value = f;
                })
            .AddTo(this.CompositeDisposable);

            // トークテキスト用ファイルドラッグオーバーコマンド
            this.DragOverTalkTextFileCommand =
                this.MakeCommand<DragEventArgs>(
                    this.ExecuteDragOverTalkTextFileCommand,
                    this.IsTalkTextEditable);

            // トークテキスト用ファイルドロップコマンド
            var dropTalkTextFileCommandHolder =
                new AsyncCommandHolder<DragEventArgs>(
                    this.IsTalkTextEditable,
                    this.ExecuteDropTalkTextFileCommand);
            this.DropTalkTextFileCommand = dropTalkTextFileCommandHolder.Command;
            dropTalkTextFileCommandHolder.IsBusy
                .Subscribe(f => dropTalkTextFileCommandBusy.Value = f)
                .AddTo(this.CompositeDisposable);
        }


        /// <summary>
        /// 表示状態のVOICEROIDプロセスコレクションを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<ReadOnlyCollection<IProcess>> VisibleProcesses
        {
            get;
        }

        /// <summary>
        /// VOICEROID選択コマンドを実行可能な状態であるか否かを取得する。
        /// </summary>
        /// <remarks>
        /// (VisibleProcesses.Value.Count >= 2) の判定結果を返す。
        /// </remarks>
        public IReadOnlyReactiveProperty<bool> IsSelectVoiceroidCommandExecutable { get; }

        /// <summary>
        /// VOICEROID選択コマンドのチップテキストを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<string> SelectVoiceroidCommandTip { get; }

        /// <summary>
        /// 表示状態のVOICEROIDプロセスコレクションの最適表示列数を取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<int> VisibleProcessesColumnCount { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスを取得する。
        /// </summary>
        public IReactiveProperty<IProcess> SelectedProcess { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスが起動中であるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsProcessStartup { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスが実行中であるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsProcessRunning { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスがトークテキストを再生中であるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsProcessPlaying { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスが実行ファイルパス登録済みであるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsProcessExecutable { get; }

        /// <summary>
        /// トークテキストを取得する。
        /// </summary>
        public IReactiveProperty<string> TalkText { get; }
        
        /// <summary>
        /// TRT's拡張：プレビュー用の設定を取得する。
        /// </summary>
        public IReactiveProperty<PreviewStyle> PreviewStyle { get; set; }

        /// <summary>
        /// TRT's拡張：IsTextSplittingとIsTextWrappingから折り返しの設定値を取得する
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsTextSplitting { get; }
        public IReadOnlyReactiveProperty<bool> IsTextWrapping { get; }
        public IReadOnlyReactiveProperty<TextWrapping> TextWrapValue { get; }

        /// <summary>
        /// TRT's拡張：プレビューの左右余白とテキスト配置より、テキストMarginを計算する
        /// </summary>
        public IReadOnlyReactiveProperty<double> PreviewLeftMargin { get; }
        public IReadOnlyReactiveProperty<double> PreviewRightMargin { get; }
        public IReadOnlyReactiveProperty<PreviewTextComponent> PreviewText { get; }
        public IReadOnlyReactiveProperty<HorizontalAlignment> PreviewHorizontal { get; }
        Thickness GetPreviewTextMargin(double leftMargin, double rightMargin, HorizontalAlignment horizontal)
        {
            var verticalMargin = 5;
            var horizontalMargin = 2;
            if (horizontal == HorizontalAlignment.Left)
                return new Thickness(leftMargin + horizontalMargin, verticalMargin, 0, verticalMargin);
            else if (horizontal == HorizontalAlignment.Center)
                return new Thickness(0, verticalMargin, 0, verticalMargin);
            else
                return new Thickness(0, verticalMargin, rightMargin + horizontalMargin, verticalMargin);
        }
        public IReadOnlyReactiveProperty<Thickness> PreviewTextMargin { get; }

        /// <summary>
        /// TRT's拡張：プレビューが複数シーンであるかを取得・設定する。
        /// Style.Triggerに利用。
        /// </summary>
        public IReactiveProperty<bool> IsMultiScenePreview { get; }
        
        /// <summary>
        /// TRT's拡張：現在表示プレビューが最初のシーンであるかを取得・設定する。
        /// Style.Triggerに利用。
        /// </summary>
        public IReactiveProperty<bool> IsFirstScene { get; set; }

        /// <summary>
        /// TRT's拡張：現在表示プレビューが最後のシーンであるかを取得・設定する。
        /// Style.Triggerに利用。
        /// </summary>
        public IReactiveProperty<bool> IsLastScene { get; set; }

        /// <summary>
        /// TRT's拡張：TalkTextのキャレット位置を取得・設定する。
        /// ImeWatermarkTextBoxに実装したプロパティBindableSelectionStartにバインドする。
        /// </summary>
        public IReactiveProperty<int> TalkTextSelectionStart { get; }

        /// <summary>
        /// TRT's拡張：InsertSplitCommand実行後のキャレット位置を保存する。
        /// </summary>
        private int AfterInsertSelectionStart { get; set; }

        /// <summary>
        /// TRT's拡張：不要な更新処理を切り上げるための前回のプレビューを保存する
        /// </summary>
        private string PrePreviewScene { get; set; }

        /// <summary>
        /// TRT's拡張：一つ前のTalkTextを保存する。
        /// </summary>
        private string PreTalkText { get; set; }

        /// <summary>
        /// TRT's拡張：一つ前のTalkTextの文字数を保存する。
        /// </summary>
        private int PreTalkTextLength { get; set; }

        /// <summary>
        /// TRT's拡張：現在表示シーンの字幕テキストを構成する行ごとのデータリスト
        /// </summary>
        public ObservableCollection<PreviewTextStore> PreviewTextList { get; set; }

        /// <summary>
        /// 入力テキスト中のキャレット位置を含むプレビューの開始位置を取得
        /// </summary>
        /// <param name="caretId">入力テキスト中のキャレット位置</param>
        /// <returns></returns>
        private int GetPreviewSceneStart(int caretId)
        {
            string talkText = this.TalkText.Value;
            string splitStr = this.PreviewStyle.Value.FileSplitString;

            var startId = 0;
            if (talkText.Length > 0)
            {
                if (caretId >= splitStr.Length)
                {
                    startId = talkText.LastIndexOf(splitStr, caretId - 2);

                    if (startId == -1)
                    {
                        startId = 0;
                    }
                    else
                    {
                        startId += splitStr.Length;
                    }
                }
                else
                {
                    startId = 0;
                }
            }
            return startId;
        }

        /// <summary>
        /// 入力テキスト中のキャレット位置を含むプレビューの終了位置を取得
        /// </summary>
        /// <param name="caretId">入力テキスト中のキャレット位置</param>
        /// <returns></returns>
        private int GetPreviewSceneEnd(int caretId)
        {
            string talkText = this.TalkText.Value;
            string splitStr = this.PreviewStyle.Value.FileSplitString;

            var endId = 0;
            if (talkText.Length > 0)
            {
                if (caretId >= splitStr.Length)
                {
                    endId = talkText.IndexOf(splitStr, caretId - splitStr.Length);
                    if (endId == -1)
                    {
                        endId = talkText.Length;
                    }
                }
                else
                {
                    endId = talkText.IndexOf(splitStr, 0);
                    if (endId == -1)
                    {
                        endId = talkText.Length;
                    }
                }
            }
            return endId;
        }

        /// <summary>
        /// プレビューの更新
        /// </summary>
        /// <param name="startId">入力テキスト中の現在表示プレビュー開始位置</param>
        /// <param name="endId">入力テキスト中の現在表示プレビュー終了位置</param>
        private void UpdatePreview(int startId, int endId)
        {
            // 拡張機能利用時のみ処理
            if (!this.PreviewStyle.Value.IsTextSplitting) return;
            
            this.IsMultiScenePreview.Value = TalkText.Value.Length > 0;
            this.IsFirstScene.Value = startId == 0;
            this.IsLastScene.Value = endId == this.TalkText.Value.Length;
            if (TalkText.Value.Length <= 0)
            {
                this.PreviewTextList.Clear();
                return;
            }

            string previewScene = this.TalkText.Value.Substring(startId, endId - startId);

            if (previewScene == this.PrePreviewScene) return;

            this.PrePreviewScene = previewScene;

            string[] lineSplitter = { this.PreviewStyle.Value.LineFeedString };
            string[] PreviewLines =
                previewScene.Split(lineSplitter, System.StringSplitOptions.RemoveEmptyEntries);

            // プレビューのItemsControl用のストアを生成
            this.PreviewTextList.Clear();
            for (int i = 0; i < PreviewLines.Length; i++)
            {
                this.PreviewTextList.Add(
                    new PreviewTextStore(PreviewLines[i], this.PreviewStyle.Value));
            }
        }

        /// <summary>
        /// UpdatePreviewを呼び出す
        /// </summary>
        private void CallUpdatePreview()
        {
            UpdatePreview(
                GetPreviewSceneStart(this.TalkTextSelectionStart.Value),
                GetPreviewSceneEnd(this.TalkTextSelectionStart.Value));
        }

        /// <summary>
        /// TalkTextのSubscribeの処理
        /// </summary>
        private void TalkTextHandle()
        {
            // SelectionStartが発火されない時にプレビューを更新
            // ・日本語入力（k → か）
            // ・Delete
            if (this.PreTalkTextLength == this.TalkText.Value.Length
                || (this.PreTalkTextLength != this.TalkText.Value.Length 
                    && this.PreTalkText != this.TalkText.Value 
                    && this.TalkTextSelectionStart.Value <= this.TalkText.Value.Length))
            {
                CallUpdatePreview();
            }
            this.PreTalkText = this.TalkText.Value;
            this.PreTalkTextLength = this.TalkText.Value.Length;
        }
        
        /// <summary>
        /// TalkTextSelectionStartのSubscribeの処理
        /// </summary>
        private void SelectionStartHandle()
        {
            // InsertSplitCommand後の処理
            if (this.AfterInsertSelectionStart != -1
                && this.AfterInsertSelectionStart != this.TalkTextSelectionStart.Value)
            {
                var start = this.AfterInsertSelectionStart;
                this.AfterInsertSelectionStart = -1;
                this.TalkTextSelectionStart.Value = start;
                return;
            }
            this.AfterInsertSelectionStart = -1;

            CallUpdatePreview();
        }

        /// <summary>
        /// TRT's拡張：設定値の変更を即座にPreviewTextList内の情報へ反映するためのメソッド
        /// UpdatePreviewの処理結果はそのままに直接PreviewTextListを更新
        /// </summary>
        private void ReflectSettingToPreview()
        {
            for (int i = 0; i < PreviewTextList.Count; i++)
            {
                this.PreviewTextList[i] =
                    new PreviewTextStore(this.PreviewTextList[i].Text, this.PreviewStyle.Value);
            }
        }
        public IReadOnlyReactiveProperty<decimal> PreviewFontSize { get; }
        public IReadOnlyReactiveProperty<int> PreviewLetterSpace { get; }
        public IReadOnlyReactiveProperty<Uri> PreviewFontUri { get; }

        /// <summary>
        /// TRT's拡張：一つ前のプレビューシーンに戻るコマンド
        /// </summary>
        public ICommand BackSceneCommand { get; }
        private void ExecuteBackSceneCommand()
        {
            if (!this.IsMultiScenePreview.Value) return;
            var currentStartId = this.GetPreviewSceneStart(this.TalkTextSelectionStart.Value);

            var backStartId = this.GetPreviewSceneStart(currentStartId - 1);
            var backEndId = this.GetPreviewSceneEnd(currentStartId - 1);
            this.TalkTextSelectionStart.Value = backStartId + 1;
        }

        /// <summary>
        /// TRT's拡張：一つ後のプレビューシーンに進むコマンド
        /// </summary>
        public ICommand NextSceneCommand { get; }
        private void ExecuteNextSceneCommand()
        {
            if (!this.IsMultiScenePreview.Value) return;
            var currentEndId = this.GetPreviewSceneEnd(this.TalkTextSelectionStart.Value);

            var nextStartId = this.GetPreviewSceneStart(currentEndId + this.PreviewStyle.Value.FileSplitString.Length);
            var nextEndId = this.GetPreviewSceneEnd(currentEndId + this.PreviewStyle.Value.FileSplitString.Length);
            this.TalkTextSelectionStart.Value = nextEndId + this.PreviewStyle.Value.FileSplitString.Length + 1;
        }

        /// <summary>
        /// TRT's拡張：キャレット位置に改行分割文字列を挿入するコマンド
        /// </summary>
        public ICommand InsertSplitCommand { get; }
        private void ExecuteInsertSplitCommand()
        {
            var currentCaret = this.TalkTextSelectionStart.Value;
            var lfStr = this.PreviewStyle.Value.LineFeedString;
            var fsStr = this.PreviewStyle.Value.FileSplitString;

            // キャレット位置の「/-」を「/--」に変換
            for (int i = 0; i < fsStr.Length; i++)
            {
                if (currentCaret <= this.TalkText.Value.Length - i
                    && currentCaret >= fsStr.Length - i
                    && this.TalkText.Value.Substring(currentCaret - (fsStr.Length - i), fsStr.Length) == fsStr)
                {
                    this.AfterInsertSelectionStart = currentCaret - (fsStr.Length - i);
                    this.TalkText.Value =
                        this.TalkText.Value
                            .Remove(currentCaret - (fsStr.Length - i), fsStr.Length);
                    return;
                }
            }
            // キャレット位置の「/--」を消去
            for (int i = 0; i < lfStr.Length; i++)
            {
                if (currentCaret <= this.TalkText.Value.Length - i
                    && currentCaret >= lfStr.Length - i
                    && this.TalkText.Value.Substring(currentCaret - (lfStr.Length - i), lfStr.Length) == lfStr)
                {
                    this.AfterInsertSelectionStart = currentCaret + (fsStr.Length - (lfStr.Length - i));
                    this.TalkText.Value =
                        this.TalkText.Value
                            .Remove(currentCaret - (lfStr.Length - i), lfStr.Length)
                            .Insert(currentCaret - (lfStr.Length - i), fsStr);
                    return;
                }
            }
            // キャレット位置に「/-」を挿入
            this.AfterInsertSelectionStart = currentCaret + lfStr.Length;
            this.TalkText.Value = this.TalkText.Value.Insert(currentCaret, lfStr);
            if (currentCaret == 0)
            {
                this.TalkTextSelectionStart.Value = currentCaret + lfStr.Length;
            }
        }


        /// <summary>
        /// トークテキストの最大許容文字数を取得する。
        /// </summary>
        /// <remarks>
        /// 0 ならば上限を定めない。
        /// TalkText に直接文字列を設定する場合、この値は考慮されない。
        /// </remarks>
        public IReactiveProperty<int> TalkTextLengthLimit { get; }

        /// <summary>
        /// トークテキストにタブ文字の入力を受け付けるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsTalkTextTabAccepted { get; }

        /// <summary>
        /// トークテキストを編集可能な状態であるか否かを取得する。
        /// </summary>
        public IReadOnlyReactiveProperty<bool> IsTalkTextEditable { get; }

        public IReadOnlyReactiveProperty<List<string>> TextList { get; }

        /// <summary>
        /// 再生や音声保存に本体側のテキストを用いるか否かを取得する。
        /// </summary>
        public IReactiveProperty<bool> UseTargetText { get; }

        /// <summary>
        /// アイドル状態であるか否かを取得する。
        /// </summary>
        /// <remarks>
        /// 再生も音声保存も実行中でなければ true となる。
        /// </remarks>
        public IReadOnlyReactiveProperty<bool> IsIdle { get; }

        /// <summary>
        /// VOICEROID選択コマンドコレクションを取得する。
        /// </summary>
        /// <remarks>
        /// 要素数は 10 固定。表示中VOICEROIDのインデックスに対応する。
        /// </remarks>
        public ReadOnlyCollection<ICommand> SelectVoiceroidCommands { get; }

        /// <summary>
        /// 前方VOICEROID選択コマンドを取得する。
        /// </summary>
        public ICommand SelectPreviousVoiceroidCommand { get; }

        /// <summary>
        /// 後方VOICEROID選択コマンドを取得する。
        /// </summary>
        public ICommand SelectNextVoiceroidCommand { get; }

        /// <summary>
        /// 実行/終了コマンドを取得する。
        /// </summary>
        public ICommand RunExitCommand { get; }

        /// <summary>
        /// 再生/停止コマンドを取得する。
        /// </summary>
        public ICommand PlayStopCommand { get; }

        /// <summary>
        /// 保存コマンドを取得する。
        /// </summary>
        public ICommand SaveCommand { get; }

        /// <summary>
        /// トークテキスト用ファイルドラッグオーバーコマンドを取得する。
        /// </summary>
        public ICommand DragOverTalkTextFileCommand { get; }

        /// <summary>
        /// トークテキスト用ファイルドロップコマンドを取得する。
        /// </summary>
        public ICommand DropTalkTextFileCommand { get; }

        /// <summary>
        /// IDataObject オブジェクトからファイルパス配列を取得する。
        /// </summary>
        /// <param name="data">IDataObject オブジェクト。</param>
        /// <returns>ファイルパス配列。取得できなければ null 。</returns>
        private static string[] GetFilePathes(IDataObject data)
        {
            if (
                data?.GetDataPresent(DataFormats.FileDrop, true) == true &&
                data.GetData(DataFormats.FileDrop, true) is string[] pathes &&
                pathes.Length > 0 &&
                pathes.All(p => File.Exists(p)))
            {
                return pathes;
            }

            return null;
        }

        /// <summary>
        /// 音声保存成功時にテキストをクリアするか否かを取得する。
        /// </summary>
        private IReadOnlyReactiveProperty<bool> IsTextClearing { get; }

        /// <summary>
        /// VOICEROIDの実行ファイルパスセットを取得する。
        /// </summary>
        private IReadOnlyReactiveProperty<VoiceroidExecutablePathSet>
        VoiceroidExecutablePathes
        {
            get;
        }

        /// <summary>
        /// 直近のアプリ状態値の設定先を取得する。
        /// </summary>
        private IReactiveProperty<IAppStatus> LastStatus { get; }

        /// <summary>
        /// ウィンドウアクティブ化サービスを取得する。
        /// </summary>
        private IWindowActivateService WindowActivateService { get; }

        /// <summary>
        /// VOICEROIDプロセスアクションサービスを取得する。
        /// </summary>
        private IVoiceroidActionService VoiceroidActionService { get; }

        /// <summary>
        /// 選択中のVOICEROIDプロセスのプロパティ変更を監視する
        /// IObservable{T} オブジェクトを作成する。
        /// </summary>
        /// <typeparam name="T">プロパティの型。</typeparam>
        /// <param name="propertySelector">プロパティセレクタ。</param>
        /// <returns>IObservable{T} オブジェクト。</returns>
        /// <remarks>
        /// VOICEROIDプロセスが非選択状態になった場合は
        /// default(T) を一度だけ返す IObservable{T} オブジェクトを作成する。
        /// </remarks>
        private IObservable<T> ObserveSelectedProcessProperty<T>(
            Expression<Func<IProcess, T>> propertySelector)
            =>
            this.SelectedProcess
                .Select(
                    p =>
                        (p == null) ?
                            Observable.Return(default(T)) :
                            p.ObserveProperty(propertySelector))
                .Switch();

        /// <summary>
        /// UI設定周りのセットアップを行う。
        /// </summary>
        /// <param name="uiConfig">UI設定値。</param>
        /// <param name="processes">VOICEROIDプロセスコレクション。</param>
        private void SetupUIConfig(
            IReadOnlyReactiveProperty<UIConfig> uiConfig,
            IReadOnlyCollection<IProcess> processes)
        {
            // 設定変更時に選択中プロセス反映
            Observable
                .CombineLatest(
                    this.VisibleProcesses,
                    uiConfig
                        .ObserveInnerProperty(c => c.VoiceroidId)
                        .DistinctUntilChanged(),
                    (vp, id) => vp.FirstOrDefault(p => p.Id == id) ?? vp.First())
                .DistinctUntilChanged()
                .Subscribe(p => this.SelectedProcess.Value = p)
                .AddTo(this.CompositeDisposable);

            // 選択中プロセス変更時処理
            this.SelectedProcess
                .Where(p => p != null)
                .Subscribe(p => uiConfig.Value.VoiceroidId = p.Id)
                .AddTo(this.CompositeDisposable);
            this.SelectedProcess
                .Where(p => p == null)
                .ObserveOnUIDispatcher()
                .Subscribe(
                    _ =>
                        this.SelectedProcess.Value =
                            processes.First(p => p.Id == uiConfig.Value.VoiceroidId))
                .AddTo(this.CompositeDisposable);

            // 実行ファイルパス反映用ローカル関数
            void pathSetter(VoiceroidId id, string path)
            {
                // パスが有効な場合のみ反映する
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    uiConfig.Value.VoiceroidExecutablePathes[id].Path = path;
                }
            }

            // UI設定変更時に実行ファイルパスを反映する
            uiConfig
                .Subscribe(
                    c =>
                    {
                        foreach (var process in processes)
                        {
                            pathSetter(process.Id, process.ExecutablePath);
                        }
                    })
                .AddTo(this.CompositeDisposable);

            // VOICEROIDプロセスの実行ファイルパスが判明したらUI設定に反映する
            foreach (var process in processes)
            {
                var id = process.Id;

                // 現在値を設定
                pathSetter(id, process.ExecutablePath);

                // 変更時に反映する
                process
                    .ObserveProperty(p => p.ExecutablePath)
                    .Subscribe(path => pathSetter(id, path))
                    .AddTo(this.CompositeDisposable);
            }
        }

        /// <summary>
        /// VOICEROID選択コマンドのチップテキストを作成する。
        /// </summary>
        /// <returns>チップテキスト。表示不要ならば null 。</returns>
        private string MakeSelectVoiceroidCommandTip() =>
            !this.IsSelectVoiceroidCommandExecutable.Value ?
                null :
                @"F1/F2 : 前/次のVOICEROIDを選択" + Environment.NewLine +
                string.Join(
                    Environment.NewLine,
                    this.VisibleProcesses.Value
                        .Take(10)
                        .Select(
                            (p, i) =>
                                @"Ctrl+" + ((i < 9) ? (i + 1) : 0) + @" : " +
                                p.Name + @" を選択"));

        /// <summary>
        /// メインウィンドウをアクティブにする。
        /// </summary>
        private Task ActivateMainWindow() => this.WindowActivateService.Run();

        /// <summary>
        /// VOICEROIDプロセスに対してアクションを行う。
        /// </summary>
        /// <param name="process">VOICEROIDプロセス。</param>
        /// <param name="action">アクション種別。</param>
        private Task RaiseVoiceroidAction(IProcess process, VoiceroidAction action) =>
            this.VoiceroidActionService.Run(process, action);

        /// <summary>
        /// 各 SelectVoiceroidCommands コマンドの実処理を行う。
        /// </summary>
        /// <param name="index">インデックス。</param>
        private void ExecuteSelectVoiceroidCommand(int index)
        {
            if (
                this.IsSelectVoiceroidCommandExecutable.Value &&
                index < this.VisibleProcesses.Value.Count)
            {
                this.SelectedProcess.Value = this.VisibleProcesses.Value[index];
            }
        }

        /// <summary>
        /// SelectPreviousVoiceroidCommand コマンドの実処理を行う。
        /// </summary>
        private void ExecuteSelectPreviousVoiceroidCommand()
        {
            var index =
                Array.IndexOf(
                    this.VisibleProcesses.Value.Select(p => p.Id).ToArray(),
                    this.SelectedProcess.Value.Id);
            if (index >= 0)
            {
                --index;
                this.ExecuteSelectVoiceroidCommand(
                    (index < 0) ? (this.VisibleProcesses.Value.Count - 1) : index);
            }
        }

        /// <summary>
        /// SelectNextVoiceroidCommand コマンドの実処理を行う。
        /// </summary>
        private void ExecuteSelectNextVoiceroidCommand()
        {
            var index =
                Array.IndexOf(
                    this.VisibleProcesses.Value.Select(p => p.Id).ToArray(),
                    this.SelectedProcess.Value.Id);
            if (index >= 0)
            {
                ++index;
                this.ExecuteSelectVoiceroidCommand(
                    (index < this.VisibleProcesses.Value.Count) ? index : 0);
            }
        }

        /// <summary>
        /// RunExitCommand コマンドの実処理を行う。
        /// </summary>
        private async Task ExecuteRunExitCommand()
        {
            var process = this.SelectedProcess.Value;
            if (process == null)
            {
                this.SetLastStatus(AppStatusType.Fail, @"処理を開始できませんでした。");
                return;
            }

            if (process.IsRunning)
            {
                // プロセス終了
                if (!(await process.Exit()))
                {
                    this.SetLastStatus(
                        process.IsDialogShowing ?
                            AppStatusType.Warning : AppStatusType.Fail,
                        @"終了処理に失敗しました。",
                        subStatusText:
                            process.IsDialogShowing ?
                                @"ダイアログが表示されたため中止しました。" : @"");
                    return;
                }

                this.SetLastStatus(
                    AppStatusType.Success,
                    @"終了 : " + process.DisplayProduct);
            }
            else
            {
                // パス情報取得
                var info = this.VoiceroidExecutablePathes.Value?[process.Id];
                if (info == null)
                {
                    this.SetLastStatus(
                        AppStatusType.Fail,
                        @"処理を開始できませんでした。");
                    return;
                }

                // 未登録か？
                if (string.IsNullOrEmpty(info.Path))
                {
                    this.SetLastStatus(
                        AppStatusType.Warning,
                        @"情報が未登録のため起動できません。",
                        subStatusText: @"一度手動で起動することで登録されます。");
                    return;
                }

                // ファイルが見つからない？
                if (!File.Exists(info.Path))
                {
                    this.SetLastStatus(
                        AppStatusType.Warning,
                        @"実行ファイルが見つかりません。",
                        subStatusText: @"一度手動で起動し直してください。");
                    return;
                }

                // プロセス起動
                try
                {
                    var errorMsg = await process.Run(info.Path);
                    if (errorMsg != null)
                    {
                        this.SetLastStatus(
                            AppStatusType.Fail,
                            @"起動処理に失敗しました。",
                            subStatusText: errorMsg);
                        return;
                    }

                    // スタートアップを終えたら1回だけメインウィンドウをアクティブにする
                    this.IsProcessStartup
                        .FirstAsync(f => !f)
                        .Subscribe(async _ => await this.ActivateMainWindow());
                }
                catch (Exception ex)
                {
                    ThreadTrace.WriteException(ex);
                    this.SetLastStatus(
                        AppStatusType.Fail,
                        @"起動処理に失敗しました。",
                        subStatusText: ex.GetType().Name + @" 例外が発生しました。");
                    return;
                }

                this.SetLastStatus(
                    AppStatusType.Success,
                    @"起動 : " + process.DisplayProduct);
            }
        }

        /// <summary>
        /// PlayStopCommand コマンド完了時処理を行う。
        /// </summary>
        /// <param name="result">コマンドの戻り値。</param>
        private async Task OnPlayStopCommandExecuted(
            AsyncPlayStopCommandHolder.CommandResult result)
        {
            if (result?.AppStatus != null)
            {
                // アプリ状態更新
                this.LastStatus.Value = result.AppStatus;
            }

            // 成否に関わらず再生処理が行われた時の処理
            if (result?.Parameter?.IsPlayAction == true)
            {
                // 対象VOICEROIDを前面へ
                var process = result.Parameter.Process;
                if (process != null)
                {
                    await this.RaiseVoiceroidAction(process, VoiceroidAction.Forward);
                }

                // メインウィンドウを前面へ
                await this.ActivateMainWindow();
            }
        }

        /// <summary>
        /// SaveCommand コマンド完了時処理を行う。
        /// </summary>
        /// <param name="result">コマンドの戻り値。</param>
        private async Task OnSaveCommandExecuted(
            AsyncSaveCommandHolder.CommandResult result)
        {
            var process = result?.Parameter?.Process;

            if (result?.AppStatus != null)
            {
                // アプリ状態更新
                this.LastStatus.Value = result.AppStatus;

                // 保存成功時のトークテキストクリア処理
                if (
                    this.IsTextClearing.Value &&
                    result.AppStatus.StatusType == AppStatusType.Success)
                {
                    if (!this.UseTargetText.Value)
                    {
                        this.TalkText.Value = @"";
                    }
                    else if (process != null)
                    {
                        // 保存処理直後だとうまくいかないことがあるので何度か試行
                        for (int i = 0; i < 10; ++i)
                        {
                            await process.SetTalkText(@"");
                            if ((await process.GetTalkText())?.Length == 0)
                            {
                                break;
                            }
                            await Task.Delay(20);
                        }
                    }
                }
            }

            // 対象VOICEROIDのタスクバーボタン点滅を止める
            if (process != null)
            {
                await this.RaiseVoiceroidAction(process, VoiceroidAction.StopFlash);
            }

            // メインウィンドウを前面へ
            await this.ActivateMainWindow();
        }

        /// <summary>
        /// DragOverTalkTextFileCommand コマンドの実処理を行う。
        /// </summary>
        /// <param name="e">ドラッグイベントデータ。</param>
        private void ExecuteDragOverTalkTextFileCommand(DragEventArgs e)
        {
            if (GetFilePathes(e?.Data) != null)
            {
                e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
                e.Handled = true;
            }
        }

        /// <summary>
        /// トークテキスト用ファイル単体のMB単位の最大許容サイズ。
        /// </summary>
        private const int TalkTextFileSizeLimitMB = 5;

        /// <summary>
        /// DropTalkTextFileCommand コマンドの実処理を行う。
        /// </summary>
        /// <param name="e">ドラッグイベントデータ。</param>
        private async Task ExecuteDropTalkTextFileCommand(DragEventArgs e)
        {
            var pathes = GetFilePathes(e?.Data);
            if (pathes == null)
            {
                return;
            }

            e.Handled = true;

            // ファイルに関する情報を取得
            var f =
                await Task.Run(
                    () =>
                    {
                        var infos = Array.ConvertAll(pathes, p => new FileInfo(p));
                        var maxInfo =
                            infos.Aggregate((i1, i2) => (i1.Length > i2.Length) ? i1 : i2);
                        return new { infos, maxInfo };
                    });

            // ファイルサイズチェック
            if (f.maxInfo.Length > TalkTextFileSizeLimitMB * 1024L * 1024)
            {
                this.SetLastStatus(
                    AppStatusType.Warning,
                    f.maxInfo.Name + @" のファイルサイズが大きすぎます。",
                    subStatusText:
                        @"許容サイズは " + TalkTextFileSizeLimitMB + @" MBまでです。");
                return;
            }

            // 全ファイル読み取り
            List<string> fileTexts = null;
            try
            {
                fileTexts = await Task.Run(() => TextFileReader.ReadAll(f.infos));
            }
            catch
            {
                this.SetLastStatus(
                    AppStatusType.Fail,
                    @"ファイルの読み取りに失敗しました。");
                return;
            }

            // 読み取り失敗したファイルがある？
            var failIndex = fileTexts.IndexOf(null);
            if (failIndex >= 0)
            {
                this.SetLastStatus(
                    AppStatusType.Warning,
                    f.infos[failIndex].Name + @" の読み取りに失敗しました。",
                    subStatusText: @"テキストファイルではない可能性があります。");
                return;
            }

            // 最大文字数
            int lenLimit = this.TalkTextLengthLimit.Value;
            if (lenLimit == 0)
            {
                lenLimit = int.MaxValue;
            }

            // 文字列連結
            var text = new StringBuilder();
            foreach (var t in fileTexts)
            {
                // 空文字列でなく、末尾が改行以外ならば改行追加
                if (text.Length > 0)
                {
                    var end = text[text.Length - 1];
                    if (end != '\r' && end != '\n')
                    {
                        text.AppendLine();
                    }
                }

                if (text.Length >= lenLimit)
                {
                    break;
                }
                text.Append(t);
            }

            // 許容文字数以上は切り捨てる
            string warnText = null;
            if (text.Length > lenLimit)
            {
                text.RemoveSurrogateSafe(lenLimit);
                warnText = text.Length + @" 文字以降は切り捨てました。";
            }

            // テキスト設定
            this.TalkText.Value = text.ToString();

            this.SetLastStatus(
                AppStatusType.Success,
                @"テキストファイルから文章を設定しました。",
                (warnText == null) ? AppStatusType.None : AppStatusType.Warning,
                warnText);
        }

        /// <summary>
        /// 直近のアプリ状態を設定する。
        /// </summary>
        /// <param name="statusType">状態種別。</param>
        /// <param name="statusText">状態テキスト。</param>
        /// <param name="subStatusType">オプショナルなサブ状態種別。</param>
        /// <param name="subStatusText">オプショナルなサブ状態テキスト。</param>
        /// <param name="subStatusCommand">オプショナルなサブ状態コマンド。</param>
        /// <param name="subStatusCommandTip">
        /// オプショナルなサブ状態コマンドのチップテキスト。
        /// </param>
        private void SetLastStatus(
            AppStatusType statusType = AppStatusType.None,
            string statusText = "",
            AppStatusType subStatusType = AppStatusType.None,
            string subStatusText = "",
            ICommand subStatusCommand = null,
            string subStatusCommandTip = "")
            =>
            this.LastStatus.Value =
                new AppStatus
                {
                    StatusType = statusType,
                    StatusText = statusText ?? "",
                    SubStatusType = subStatusType,
                    SubStatusText = subStatusText ?? "",
                    SubStatusCommand = subStatusCommand,
                    SubStatusCommandTip =
                        string.IsNullOrEmpty(subStatusCommandTip) ?
                            null : subStatusCommandTip,
                };

        #region デザイン時用定義

        /// <summary>
        /// デザイン時用コンストラクタ。
        /// </summary>
        [Obsolete(@"Design time only.")]
        public VoiceroidViewModel()
            :
            this(
                new ProcessFactory().Processes,
                new ReactiveProperty<bool>(true),
                new ReactiveProperty<TalkTextReplaceConfig>(new TalkTextReplaceConfig()),
                new ReactiveProperty<ExoConfig>(new ExoConfig()),
                new ReactiveProperty<AppConfig>(new AppConfig()),
                new ReactiveProperty<UIConfig>(new UIConfig()),
                new ReactiveProperty<IAppStatus>(new AppStatus()),
                new ReactiveProperty<bool>(true),
                NullServices.WindowActivate,
                NullServices.VoiceroidAction,
                NullServices.AviUtlFileDrop)
        {
        }

        #endregion
    }
}
