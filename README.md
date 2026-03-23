#  RadarGraphs

![Platform](https://img.shields.io/badge/platform-Windows-blue)
![Framework](https://img.shields.io/badge/.NET-8.0-purple)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)
![Language](https://img.shields.io/badge/language-C%23-239120)
![Status]([https://img.shields.io/badge/status-Development-orange](https://img.shields.io/badge/status-Prototype%20%2F%20Internal-orange))

---

##  Overview

**RadarGraphs** is a WPF desktop application built in **C# (.NET 8)** for visualizing data using different chart types such as **bar charts** and **pie charts**.

The project focuses on simple and interactive data visualization in a desktop environment, with a modular structure allowing easy expansion with additional chart types.

---

##  Features

###  Chart Visualization
- **Bar chart rendering**
  - Display of values in a column-based format
  - Easy comparison between categories

- **Pie chart rendering**
  - Visualization of proportional data
  - Clear representation of percentages and distribution

###  Multiple Windows
- Separate windows for different chart types:
  - `BarChartWindow`
  - `PieChartWindow`
- Independent rendering logic for each visualization type

###  User Interface
- Built with **WPF**
- Responsive layout
- Simple and clean UI for displaying charts

---

##  How It Works

The application is structured around multiple windows, each responsible for rendering a specific type of chart.

### Core workflow:

1. **Data Preparation**
   - Input data is defined within the application
   - Values are prepared for visualization

2. **Chart Selection**
   - User selects or opens a specific chart window

3. **Rendering**
   - Chart is drawn using WPF graphics (Canvas / Shapes)
   - Data is converted into visual elements (bars, slices)

4. **Display**
   - UI updates and renders the chart in real time

---

##  Data Flow

1. Application starts in `MainWindow`  
2. User opens a specific chart window (bar or pie)  
3. Data is passed to the selected window  
4. Visualization logic converts data into graphical elements  
5. Chart is rendered on screen  

---

##  Requirements

- Windows  
- .NET 8 SDK  
- Visual Studio 2022 or newer  

---

##  Getting Started

1. Clone the repository  
2. Open the project in Visual Studio  
3. Restore dependencies  
4. Build and run the application  

---

##  Technologies Used

- **C#**
- **WPF (Windows Presentation Foundation)**
- **.NET 8**
- XAML for UI definition

---

##  Use Case

This project is suitable for:

- learning WPF rendering techniques,
- building simple data visualization tools,
- experimenting with custom chart implementations,
- extending into more advanced graphing systems.

---

##  Notes

The project is designed as a lightweight visualization tool and a foundation for further development of custom chart components.

---

##  Author: Michal Švrček

Developed as part of data visualization and testing for our radars.
