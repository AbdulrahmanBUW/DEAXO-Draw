using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Data;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

// Aliases to resolve namespace conflicts
using WpfGrid = System.Windows.Controls.Grid;
using WpfBinding = System.Windows.Data.Binding;
using WpfTextBox = System.Windows.Controls.TextBox;

namespace CustomRevitCommand
{
    // WPF View Selection Window - CORRECTED VERSION
    public class ViewSelectionWindow : Window
    {
        public ObservableCollection<ViewItem> ViewItems { get; set; }
        public List<View> SelectedViews { get; private set; }
        private Document _document;
        private View _currentView; // Store current active view
        private System.ComponentModel.ICollectionView _viewsCollectionView;

        // UI Controls
        private ListView _viewsListView;
        private Button _selectAllButton;
        private Button _selectNoneButton;
        private Button _applyButton;
        private Button _cancelButton;
        private WpfTextBox _searchTextBox;
        private TextBlock _selectionCountText; // NEW: Selection counter

        public ViewSelectionWindow(Document document, View currentView)
        {
            _document = document;
            _currentView = currentView; // Store current view
            ViewItems = new ObservableCollection<ViewItem>();
            SelectedViews = new List<View>();

            InitializeComponent();
            LoadViews();

            // Set up collection view for filtering
            _viewsCollectionView = System.Windows.Data.CollectionViewSource.GetDefaultView(ViewItems);
            _viewsListView.ItemsSource = _viewsCollectionView;

            // Auto-select current view if it's dimensionable
            AutoSelectCurrentView();
            UpdateSelectionCount(); // Initial count update
        }

        private void InitializeComponent()
        {
            // Window properties
            Title = "Select Views - Auto Dimension Tool";
            Height = 400;
            Width = 500;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            MinHeight = 300;
            MinWidth = 400;

            // Main grid
            WpfGrid mainGrid = new WpfGrid();
            mainGrid.Margin = new Thickness(10);

            // Define rows
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Title
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Buttons  
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Search box
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // List
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Selection count + info
            mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // Action buttons

            // Title
            TextBlock titleText = new TextBlock
            {
                Text = "Select views to apply Auto-Dimension command:",
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            WpfGrid.SetRow(titleText, 0);
            mainGrid.Children.Add(titleText);

            // Selection buttons panel
            StackPanel buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 10)
            };

            _selectAllButton = new Button
            {
                Content = "Select All",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(5, 5, 5, 5),
                MinWidth = 80
            };
            _selectAllButton.Click += SelectAllButton_Click;

            _selectNoneButton = new Button
            {
                Content = "Select None",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(5, 5, 5, 5),
                MinWidth = 80
            };
            _selectNoneButton.Click += SelectNoneButton_Click;

            buttonPanel.Children.Add(_selectAllButton);
            buttonPanel.Children.Add(_selectNoneButton);
            WpfGrid.SetRow(buttonPanel, 1);
            mainGrid.Children.Add(buttonPanel);

            // Search box
            StackPanel searchPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 5, 0, 10)
            };

            TextBlock searchLabel = new TextBlock
            {
                Text = "Search views:",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };

            _searchTextBox = new WpfTextBox
            {
                Width = 200,
                Height = 25,
                VerticalAlignment = VerticalAlignment.Center
            };
            _searchTextBox.TextChanged += SearchTextBox_TextChanged;

            // Add placeholder text behavior
            _searchTextBox.GotFocus += (s, e) => {
                if (_searchTextBox.Text == "Type to search views...")
                {
                    _searchTextBox.Text = "";
                    _searchTextBox.Foreground = Brushes.Black;
                }
            };
            _searchTextBox.LostFocus += (s, e) => {
                if (string.IsNullOrWhiteSpace(_searchTextBox.Text))
                {
                    _searchTextBox.Text = "Type to search views...";
                    _searchTextBox.Foreground = Brushes.Gray;
                }
            };

            // Set initial placeholder
            _searchTextBox.Text = "Type to search views...";
            _searchTextBox.Foreground = Brushes.Gray;

            Button clearButton = new Button
            {
                Content = "Clear",
                Width = 50,
                Height = 25,
                Margin = new Thickness(5, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            clearButton.Click += ClearButton_Click;

            searchPanel.Children.Add(searchLabel);
            searchPanel.Children.Add(_searchTextBox);
            searchPanel.Children.Add(clearButton);
            WpfGrid.SetRow(searchPanel, 2);
            mainGrid.Children.Add(searchPanel);

            // Views list with border (FIXED SCROLLING)
            Border listBorder = new Border
            {
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Background = Brushes.White
            };

            // Remove the outer ScrollViewer - ListView has its own internal scrolling
            _viewsListView = new ListView
            {
                SelectionMode = SelectionMode.Multiple,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            // Set attached property for better scrolling
            ScrollViewer.SetCanContentScroll(_viewsListView, true);

            // Create data template for list items
            DataTemplate itemTemplate = new DataTemplate();
            FrameworkElementFactory gridFactory = new FrameworkElementFactory(typeof(WpfGrid));

            // Grid columns
            FrameworkElementFactory col1 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col1.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);
            FrameworkElementFactory col2 = new FrameworkElementFactory(typeof(ColumnDefinition));
            col2.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));
            gridFactory.AppendChild(col1);
            gridFactory.AppendChild(col2);

            // Checkbox
            FrameworkElementFactory checkboxFactory = new FrameworkElementFactory(typeof(CheckBox));
            checkboxFactory.SetBinding(CheckBox.IsCheckedProperty, new WpfBinding("IsSelected"));
            checkboxFactory.SetValue(WpfGrid.ColumnProperty, 0);
            checkboxFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 2, 5, 2));
            checkboxFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            checkboxFactory.AddHandler(CheckBox.CheckedEvent, new RoutedEventHandler(CheckBox_CheckedChanged));
            checkboxFactory.AddHandler(CheckBox.UncheckedEvent, new RoutedEventHandler(CheckBox_CheckedChanged));
            gridFactory.AppendChild(checkboxFactory);

            // Text
            FrameworkElementFactory textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new WpfBinding("DisplayName"));
            textFactory.SetValue(WpfGrid.ColumnProperty, 1);
            textFactory.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            textFactory.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 0, 5, 0));
            gridFactory.AppendChild(textFactory);

            itemTemplate.VisualTree = gridFactory;
            _viewsListView.ItemTemplate = itemTemplate;
            // ItemsSource will be set in constructor after LoadViews()

            // Add ListView directly to border (no ScrollViewer wrapper)
            listBorder.Child = _viewsListView;
            WpfGrid.SetRow(listBorder, 3);
            mainGrid.Children.Add(listBorder);

            // NEW: Selection count and info text panel
            StackPanel infoPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 5)
            };

            _selectionCountText = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.DarkBlue,
                VerticalAlignment = VerticalAlignment.Center
            };

            TextBlock infoText = new TextBlock
            {
                Text = " views selected for auto-dimensioning",
                Foreground = Brushes.DarkBlue,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            };

            infoPanel.Children.Add(_selectionCountText);
            infoPanel.Children.Add(infoText);
            WpfGrid.SetRow(infoPanel, 4);
            mainGrid.Children.Add(infoPanel);

            // Action buttons panel
            StackPanel actionPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0)
            };

            _applyButton = new Button
            {
                Content = "Execute Auto-Dimension",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(5, 5, 5, 5),
                MinWidth = 120,
                IsDefault = true
            };
            _applyButton.Click += ApplyButton_Click;

            _cancelButton = new Button
            {
                Content = "Cancel",
                Padding = new Thickness(15, 5, 15, 5),
                Margin = new Thickness(5, 5, 5, 5),
                MinWidth = 80,
                IsCancel = true
            };
            _cancelButton.Click += CancelButton_Click;

            actionPanel.Children.Add(_applyButton);
            actionPanel.Children.Add(_cancelButton);
            WpfGrid.SetRow(actionPanel, 5);
            mainGrid.Children.Add(actionPanel);

            Content = mainGrid;
        }

        private void LoadViews()
        {
            try
            {
                var allViews = new FilteredElementCollector(_document)
                    .OfClass(typeof(View))
                    .Cast<View>();

                var filteredViews = allViews
                    .Where(v => CanViewBeDimensioned(v))
                    .OrderBy(v => GetViewTypeString(v.ViewType))
                    .ThenBy(v => v.Name);

                foreach (View view in filteredViews)
                {
                    // Additional safety check - skip any 3D views that might have slipped through
                    if (view.ViewType == ViewType.ThreeD)
                    {
                        System.Diagnostics.Debug.WriteLine($"Skipping 3D view: {view.Name}");
                        continue;
                    }

                    ViewItems.Add(new ViewItem
                    {
                        View = view,
                        Name = view.Name,
                        ViewType = view.ViewType,
                        IsSelected = false,
                        DisplayName = $"{GetViewTypeString(view.ViewType)}: {view.Name}"
                    });
                }

                System.Diagnostics.Debug.WriteLine($"Loaded {ViewItems.Count} views (3D views excluded)");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Error", $"Error loading views: {ex.Message}");
            }
        }

        // NEW: Auto-select current view if it's dimensionable
        private void AutoSelectCurrentView()
        {
            if (_currentView != null && CanViewBeDimensioned(_currentView))
            {
                var currentViewItem = ViewItems.FirstOrDefault(v => v.View.Id == _currentView.Id);
                if (currentViewItem != null)
                {
                    currentViewItem.IsSelected = true;
                    System.Diagnostics.Debug.WriteLine($"Auto-selected current view: {_currentView.Name}");
                }
            }
        }

        // NEW: Update selection count display
        private void UpdateSelectionCount()
        {
            int selectedCount = ViewItems.Count(v => v.IsSelected);
            int totalCount = ViewItems.Count;
            _selectionCountText.Text = $"{selectedCount} from {totalCount}";
        }

        // NEW: Handle checkbox changes to update count
        private void CheckBox_CheckedChanged(object sender, RoutedEventArgs e)
        {
            UpdateSelectionCount();
        }

        private bool CanViewBeDimensioned(View view)
        {
            if (view == null || view.IsTemplate) return false;

            // Explicitly exclude 3D views first
            if (view.ViewType == ViewType.ThreeD)
                return false;

            // Only include specific 2D view types that support dimensioning
            switch (view.ViewType)
            {
                case ViewType.FloorPlan:
                case ViewType.CeilingPlan:
                case ViewType.AreaPlan:
                case ViewType.Section:
                case ViewType.Elevation:
                case ViewType.Detail:
                    return true;
                default:
                    return false; // Exclude everything else including 3D views
            }
        }

        private string GetViewTypeString(ViewType viewType)
        {
            switch (viewType)
            {
                case ViewType.FloorPlan: return "Floor Plan";
                case ViewType.CeilingPlan: return "Ceiling Plan";
                case ViewType.AreaPlan: return "Area Plan";
                case ViewType.Section: return "Section";
                case ViewType.Elevation: return "Elevation";
                case ViewType.Detail: return "Detail";
                default: return viewType.ToString();
            }
        }

        private void SearchTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            if (_viewsCollectionView != null)
            {
                string searchText = _searchTextBox.Text.ToLower();

                // Don't filter if showing placeholder text
                if (string.IsNullOrEmpty(searchText) || searchText == "type to search views...")
                {
                    // Clear filter to show all views
                    _viewsCollectionView.Filter = null;
                }
                else
                {
                    // Apply filter to show only matching views
                    _viewsCollectionView.Filter = item =>
                    {
                        if (item is ViewItem viewItem)
                        {
                            return viewItem.DisplayName.ToLower().Contains(searchText);
                        }
                        return false;
                    };
                }
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _searchTextBox.Text = "Type to search views...";
            _searchTextBox.Foreground = Brushes.Gray;
            _searchTextBox.Focus(); // This will trigger the GotFocus event to clear placeholder
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            // Select all currently visible items (after filtering)
            if (_viewsCollectionView != null)
            {
                foreach (ViewItem item in _viewsCollectionView)
                {
                    item.IsSelected = true;
                }
                UpdateSelectionCount();
            }
        }

        private void SelectNoneButton_Click(object sender, RoutedEventArgs e)
        {
            // Deselect all currently visible items (after filtering)
            if (_viewsCollectionView != null)
            {
                foreach (ViewItem item in _viewsCollectionView)
                {
                    item.IsSelected = false;
                }
                UpdateSelectionCount();
            }
        }

        private void ApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedViewItems = ViewItems.Where(v => v.IsSelected).ToList();

            if (selectedViewItems.Count == 0)
            {
                TaskDialog.Show("Warning", "Please select at least one view.");
                return;
            }

            SelectedViews = selectedViewItems.Select(v => v.View).ToList();

            // SIMPLIFIED: Just confirm execution without extensive message
            TaskDialogResult result = TaskDialog.Show(
                "Execute Auto-Dimension",
                $"Execute Auto-Dimension on {SelectedViews.Count} selected view(s)?",
                TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No);

            if (result == TaskDialogResult.Yes)
            {
                DialogResult = true;
                Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

    // ViewModel class for view items
    public class ViewItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public View View { get; set; }
        public string Name { get; set; }
        public ViewType ViewType { get; set; }
        public string DisplayName { get; set; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                _isSelected = value;
                OnPropertyChanged(nameof(IsSelected));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}